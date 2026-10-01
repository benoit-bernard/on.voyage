using System.Globalization;
using System.Threading.RateLimiting;
using OnVoyage.ServiceDefaults.Security;

namespace OnVoyage.Gateway.Edge;

internal static class EdgeProtection
{
    private static readonly string[] ExemptPrefixes = ["/health", "/alive"];

    private static bool IsExempt(HttpContext context) => ExemptPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.Ordinal));

    /// <summary>Refuses crawlers listed in <c>security.blocked_user_agents</c> (F-24), on every path including <c>/api</c>.</summary>
    public static IApplicationBuilder UseUserAgentBlocking(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 0 && !IsExempt(context))
        {
            var blocked = context.RequestServices.GetRequiredService<EdgeSettingsStore>().Current.BlockedUserAgents;
            if (blocked.Any(token => userAgent.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://on.voyage/problems/blocked_client",
                    title = "Automated access is not allowed.",
                    status = StatusCodes.Status403Forbidden,
                });
                return;
            }
        }

        await next(context);
    });

    /// <summary>
    /// Sliding-window limits: per client address (300/min) and, once authenticated, per traveler (120/min) — SEC-04. The limit is part of
    /// the partition key, so a new value from Platform takes effect immediately for new windows.
    /// </summary>
    public static IServiceCollection AddEdgeRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (IsExempt(context))
                    {
                        return RateLimitPartition.GetNoLimiter("exempt");
                    }

                    var limit = context.RequestServices.GetRequiredService<EdgeSettingsStore>().Current.RatePerIpPerMinute;
                    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetSlidingWindowLimiter($"ip:{address}:{limit}", _ => Window(limit));
                }),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (IsExempt(context) || context.User.TravelerId() is not { } traveler)
                    {
                        return RateLimitPartition.GetNoLimiter("no-traveler");
                    }

                    var limit = context.RequestServices.GetRequiredService<EdgeSettingsStore>().Current.RatePerTravelerPerMinute;
                    return RateLimitPartition.GetSlidingWindowLimiter($"traveler:{traveler:N}:{limit}", _ => Window(limit));
                }));

            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;

                // A chained limiter does not always surface the inner lease's hint; the window length is a safe upper bound.
                var wait = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : WindowLength;
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                response.ContentType = "application/problem+json";
                await response.WriteAsJsonAsync(
                    new { type = "https://on.voyage/problems/rate_limited", title = "Too many requests. Slow down.", status = StatusCodes.Status429TooManyRequests },
                    cancellationToken);
            };
        });

        return services;
    }

    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(1);

    private static SlidingWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = WindowLength,
        SegmentsPerWindow = 6,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

using System.Text.Json;

namespace OnVoyage.Web.Public.Seo;

/// <summary>Crawlers refused with 403 (F-24, annexe A): the same list as the Gateway, from Platform's shipped configuration unless overridden.</summary>
public sealed class BlockedCrawlers
{
    public BlockedCrawlers(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetSection("Security:BlockedUserAgents").Get<string[]>();
        Tokens = configured is { Length: > 0 } ? configured : LoadDefault(environment);
    }

    public IReadOnlyList<string> Tokens { get; }

    public bool Matches(string userAgent) => userAgent.Length > 0 && Tokens.Any(token => userAgent.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string[] LoadDefault(IHostEnvironment environment)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "default-config.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{path} is missing: it carries security.blocked_user_agents.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("security").GetProperty("blocked_user_agents").EnumerateArray().Select(item => item.GetString()!)];
    }
}

internal static class Middleware
{
    public static IApplicationBuilder UseCrawlerBlocking(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
            && !context.Request.Path.StartsWithSegments("/alive", StringComparison.Ordinal)
            && context.RequestServices.GetRequiredService<BlockedCrawlers>().Matches(context.Request.Headers.UserAgent.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Automated access is not allowed.");
            return;
        }

        await next(context);
    });

    /// <summary>TDMRep (annexe B): the reservation header and policy on every HTML response.</summary>
    public static IApplicationBuilder UseTdmReservation(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var settings = context.RequestServices.GetRequiredService<PublicSettings>();
                context.Response.Headers["tdm-reservation"] = "1";
                context.Response.Headers["tdm-policy"] = settings.Absolute(PublicSettings.TdmPolicyPath);
            }

            return Task.CompletedTask;
        });
        return next(context);
    });
}

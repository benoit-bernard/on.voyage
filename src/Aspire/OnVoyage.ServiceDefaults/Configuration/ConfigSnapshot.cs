using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OnVoyage.ServiceDefaults.Configuration;

/// <summary>
/// Read side of a service's local <c>config_snapshot</c> projection of Platform's remote configuration (§18).
/// Each service implements the storage; the lookup rules and the version gate below are shared.
/// </summary>
public interface IConfigSnapshot
{
    /// <summary>Value of a top-level key as raw JSON, or <c>null</c> when the snapshot has not received it yet.</summary>
    ValueTask<string?> GetJsonAsync(string key, CancellationToken cancellationToken);
}

public static class ConfigSnapshotExtensions
{
    /// <summary>Reads <c>key.sub.path</c>: the first segment is the stored key, the rest navigates the JSON.</summary>
    public static async ValueTask<string?> GetStringAsync(this IConfigSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        var segments = path.Split('.');
        var json = await snapshot.GetJsonAsync(segments[0], cancellationToken);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;
        foreach (var segment in segments.Skip(1))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
    }
}

/// <summary>Answers <c>426 Upgrade Required</c> when <c>X-App-Version</c> is below <c>app.min_app_version</c> (§12.1).</summary>
public static class MinAppVersionMiddleware
{
    public static IApplicationBuilder UseMinAppVersionGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
            && Version.TryParse(context.Request.Headers["X-App-Version"].ToString(), out var appVersion))
        {
            var snapshot = context.RequestServices.GetRequiredService<IConfigSnapshot>();
            var minimum = await snapshot.GetStringAsync("app.min_app_version", context.RequestAborted);
            if (Version.TryParse(minimum, out var required) && appVersion < required)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://on.voyage/problems/upgrade_required",
                    title = "This app version is no longer supported.",
                    status = StatusCodes.Status426UpgradeRequired,
                    minAppVersion = minimum,
                });
                return;
            }
        }

        await next(context);
    });
}

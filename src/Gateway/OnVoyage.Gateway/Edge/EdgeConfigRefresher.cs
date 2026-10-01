using System.Net.Http.Headers;
using System.Text.Json;
using OnVoyage.ServiceDefaults.Security;

namespace OnVoyage.Gateway.Edge;

/// <summary>
/// Pulls <c>GET /api/platform/v1/config?scope=edge</c> on the internal network (§9.3) and keeps the last good value when Platform is
/// down. The Gateway authenticates with a short-lived token carrying the <c>internal</c> role.
/// </summary>
internal sealed class EdgeConfigRefresher(
    IHttpClientFactory clients, EdgeSettingsStore store, IConfiguration configuration, TimeProvider clock, ILogger<EdgeConfigRefresher> logger) : BackgroundService
{
    public const string ClientName = "platform-edge";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(stoppingToken);

            var seconds = configuration.GetValue<int?>("Gateway:EdgeConfigRefreshSeconds") ?? store.Current.RefreshSeconds;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600)), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/platform/v1/config?scope=edge");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", InternalTokens.Mint(configuration["Auth:JwtSecret"]!, "gateway", clock));

            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (Parse(document.RootElement) is { } settings)
            {
                store.Update(settings);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Type only: messages of HTTP errors can carry the request URL. Any failure keeps the last good settings; the loop must never stop the host.
            logger.LogWarning("Edge configuration refresh failed ({Error}); keeping the last known settings.", ex.GetType().Name);
        }
    }

    /// <summary>Reads <c>config.security</c>; a missing or invalid field falls back to the value already in force.</summary>
    internal EdgeSettings? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("config", out var config) || !config.TryGetProperty("security", out var security) || security.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var current = store.Current;
        var agents = current.BlockedUserAgents;
        if (security.TryGetProperty("blocked_user_agents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            agents = [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())];
        }

        return new EdgeSettings(
            Positive(security, "rate_per_traveler_per_min", current.RatePerTravelerPerMinute),
            Positive(security, "rate_per_ip_per_min", current.RatePerIpPerMinute),
            agents,
            Positive(security, "edge_config_refresh_seconds", current.RefreshSeconds));
    }

    private static int Positive(JsonElement section, string name, int fallback) =>
        section.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) && value > 0 ? value : fallback;
}

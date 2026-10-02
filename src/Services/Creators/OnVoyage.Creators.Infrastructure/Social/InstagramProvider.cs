using System.Globalization;
using System.Net;
using System.Text.Json;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>
/// Instagram API with Instagram Login (F-27, T-1207), scope <c>instagram_business_basic</c>: professional accounts only.
///
/// UNVERIFIED AGAINST THE REAL API. It was written from Meta's public documentation of the endpoints below, without network access and before the
/// Meta application review (H-008): the field names, the shape of the short-lived token answer (flat object or <c>data[]</c>, both read), the
/// absence of a documented PKCE parameter and of a revoke endpoint are assumptions to check with the first real connection. The feature stays
/// off (<c>Creators:Social:Instagram:Enabled</c>) until then.
///
/// Endpoints: authorization <c>https://www.instagram.com/oauth/authorize</c>; short-lived token <c>POST https://api.instagram.com/oauth/access_token</c>;
/// long-lived token <c>GET https://graph.instagram.com/access_token?grant_type=ig_exchange_token</c> (60 days); renewal
/// <c>GET https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token</c>; profile <c>GET https://graph.instagram.com/me</c>;
/// contents <c>GET https://graph.instagram.com/me/media</c>.
/// </summary>
internal sealed class InstagramProvider(HttpClient http, PlatformOptions options, TimeProvider clock) : ISocialProvider
{
    public const string Scope = "instagram_business_basic";

    private const string AuthorizeEndpoint = "https://www.instagram.com/oauth/authorize";
    private const string ShortLivedEndpoint = "https://api.instagram.com/oauth/access_token";
    private const string GraphHost = "graph.instagram.com";

    public string Platform => ContentPlatforms.Instagram;

    public PlatformOptions Options => options;

    public bool UsesPkce => options.UsePkce;

    // Long-lived tokens last 60 days and can be renewed once they are 24 hours old: renew in the last week.
    public TimeSpan RefreshMargin => TimeSpan.FromDays(7);

    public Uri AuthorizeUrl(string state, string? codeChallenge)
    {
        var query = $"client_id={Uri.EscapeDataString(options.ClientId!)}&redirect_uri={Uri.EscapeDataString(options.RedirectUri!)}&response_type=code&scope={Scope}&state={Uri.EscapeDataString(state)}";
        if (codeChallenge is not null)
        {
            query += $"&code_challenge={codeChallenge}&code_challenge_method=S256";
        }

        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code, string? codeVerifier, CancellationToken cancellationToken)
    {
        Dictionary<string, string> form = new()
        {
            ["client_id"] = options.ClientId!,
            ["client_secret"] = options.ClientSecret!,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = options.RedirectUri!,
            ["code"] = code,
        };
        if (codeVerifier is not null)
        {
            form["code_verifier"] = codeVerifier;
        }

        using var response = await http.PostAsync(ShortLivedEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        using var shortLived = await ReadAsync(response, cancellationToken);
        var root = shortLived.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            root = data[0]; // some versions wrap the answer in data[]
        }

        var token = root.Str("access_token") ?? throw new ProviderException("no_access_token", false);
        var permissions = (root.Str("permissions") ?? Scope).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);

        // The short-lived token (one hour) is exchanged at once for a long-lived one (60 days), which can then be renewed.
        var exchange = $"https://{GraphHost}/access_token?grant_type=ig_exchange_token&client_secret={Uri.EscapeDataString(options.ClientSecret!)}&access_token={Uri.EscapeDataString(token)}";
        using var longResponse = await http.GetAsync(exchange, cancellationToken);
        using var longLived = await ReadAsync(longResponse, cancellationToken);
        return new TokenSet(longLived.RootElement.Str("access_token") ?? throw new ProviderException("no_access_token", false), null, ExpiresIn(longLived.RootElement), permissions);
    }

    public async Task<TokenSet> RefreshAsync(TokenSet current, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"https://{GraphHost}/refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(current.AccessToken)}", cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        return current with { AccessToken = json.RootElement.Str("access_token") ?? throw new ProviderException("no_access_token", true), ExpiresAt = ExpiresIn(json.RootElement) };
    }

    public async Task<SocialProfile> GetProfileAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"https://{GraphHost}/{Version()}me?fields=user_id,username,account_type", tokens, cancellationToken);
        var root = json.RootElement;
        var id = root.Str("user_id") ?? root.Str("id") ?? throw new ProviderException("no_user", false);
        var accountType = root.Str("account_type");

        // Only professional accounts (Business or Creator) can use the API; a personal account is refused (F-27).
        return new SocialProfile(id, root.Str("username") ?? id, accountType is null || accountType.Equals("BUSINESS", StringComparison.OrdinalIgnoreCase) || accountType.Equals("MEDIA_CREATOR", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<RemoteFetch> ListAsync(TokenSet tokens, string externalUserId, int limit, CancellationToken cancellationToken)
    {
        List<RemoteContent> items = [];
        string? next = $"https://{GraphHost}/{Version()}me/media?fields=id,caption,media_type,permalink,thumbnail_url,media_url,timestamp&limit={Math.Min(limit, 50).ToString(CultureInfo.InvariantCulture)}";
        while (next is not null && items.Count < limit)
        {
            using var json = await GetJsonAsync(next, tokens, cancellationToken);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var media in data.EnumerateArray())
                {
                    if (ToContent(media) is { } content && items.Count < limit)
                    {
                        items.Add(content);
                    }
                }
            }

            // The next-page address carries the token in its query: it is only followed on the platform's own host.
            next = json.RootElement.TryGetProperty("paging", out var paging) && paging.Str("next") is { } address
                && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == GraphHost ? address : null;
        }

        return new RemoteFetch(FetchStatus.Ok, items, next is null);
    }

    // Instagram has no documented endpoint to revoke an access token: disconnecting deletes ours; the creator can also remove the app in Instagram's settings.
    public Task RevokeAsync(TokenSet tokens, CancellationToken cancellationToken) => Task.CompletedTask;

    internal static RemoteContent? ToContent(JsonElement media)
    {
        var id = media.Str("id");
        var permalink = media.Str("permalink");
        if (id is null || permalink is null)
        {
            return null;
        }

        var caption = media.Str("caption");
        var kind = media.Str("media_type") switch
        {
            "VIDEO" => ContentKinds.Video,
            "CAROUSEL_ALBUM" => ContentKinds.Carousel,
            _ => ContentKinds.Photo,
        };
        var published = DateTimeOffset.TryParse(media.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : (DateTimeOffset?)null;
        var firstLine = (caption ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var title = firstLine ?? (published is { } at ? $"Publication Instagram du {at.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" : "Publication Instagram");
        return new RemoteContent(id, permalink, title, caption, kind, published, null, media.Str("thumbnail_url") ?? media.Str("media_url"), []);
    }

    private string Version() => string.IsNullOrWhiteSpace(options.ApiVersion) ? string.Empty : options.ApiVersion.Trim('/') + "/";

    private DateTimeOffset? ExpiresIn(JsonElement root) =>
        root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt64(out var value) ? clock.GetUtcNow().AddSeconds(value) : null;

    private async Task<JsonDocument> GetJsonAsync(string address, TokenSet tokens, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>Reads a JSON answer; a platform error becomes a <see cref="ProviderException"/> (OAuth error 190, or a 401, means the token is dead).</summary>
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        JsonDocument? json = null;
        try
        {
            json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Not JSON: handled below from the status code.
        }

        if (response.IsSuccessStatusCode && json is not null && !json.RootElement.TryGetProperty("error", out _))
        {
            return json;
        }

        var error = json is not null && json.RootElement.TryGetProperty("error", out var node) ? node : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var number) && number.TryGetInt32(out var value) ? value : 0;
        var reauthorization = response.StatusCode == HttpStatusCode.Unauthorized || code == 190 || (error.ValueKind == JsonValueKind.Object && error.Str("type") == "OAuthException" && code is 190 or 102);
        json?.Dispose();
        throw new ProviderException(reauthorization ? "token_rejected" : $"http_{(int)response.StatusCode}", reauthorization);
    }
}

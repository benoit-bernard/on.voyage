using System.Globalization;
using System.Net;
using System.Text.Json;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>
/// YouTube (F-27, T-1208): Google OAuth 2.0 (code + PKCE, offline access) with the single scope <c>youtube.readonly</c>, then the YouTube Data API v3.
///
/// UNVERIFIED AGAINST THE REAL API. Written from Google's public documentation without network access and before the OAuth verification of the
/// application (H-008). The feature stays off (<c>Creators:Social:YouTube:Enabled</c>) until a real connection has been tried.
///
/// Calls: authorization <c>https://accounts.google.com/o/oauth2/v2/auth</c>; token and renewal <c>POST https://oauth2.googleapis.com/token</c>;
/// revocation <c>POST https://oauth2.googleapis.com/revoke</c>; the channel <c>channels.list?mine=true</c> (1 quota unit); the uploads
/// <c>playlistItems.list</c> (1 unit per 50) and <c>videos.list</c> (1 unit per 50) — about 9 units for 200 videos, against a quota of 10 000 a day.
/// <c>recordingDetails.location</c> is deliberately not used (deprecated since 2017): places come from the text and the chapters (F-28).
/// </summary>
internal sealed class YouTubeProvider(HttpClient http, PlatformOptions options, TimeProvider clock) : ISocialProvider
{
    public const string Scope = "https://www.googleapis.com/auth/youtube.readonly";

    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string Api = "https://www.googleapis.com/youtube/v3";
    private static readonly string[] ThumbnailSizes = ["high", "medium", "standard", "default"];

    public string Platform => ContentPlatforms.YouTube;

    public PlatformOptions Options => options;

    public bool UsesPkce => options.UsePkce;

    public TimeSpan RefreshMargin => TimeSpan.FromMinutes(5);

    public Uri AuthorizeUrl(string state, string? codeChallenge)
    {
        var query = $"client_id={Uri.EscapeDataString(options.ClientId!)}&redirect_uri={Uri.EscapeDataString(options.RedirectUri!)}&response_type=code&scope={Uri.EscapeDataString(Scope)}&access_type=offline&prompt=consent&include_granted_scopes=false&state={Uri.EscapeDataString(state)}";
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
            ["code"] = code,
            ["client_id"] = options.ClientId!,
            ["client_secret"] = options.ClientSecret!,
            ["redirect_uri"] = options.RedirectUri!,
            ["grant_type"] = "authorization_code",
        };
        if (codeVerifier is not null)
        {
            form["code_verifier"] = codeVerifier;
        }

        using var json = await PostTokenAsync(form, cancellationToken);
        var root = json.RootElement;
        var scopes = (root.Str("scope") ?? Scope).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(Scope))
        {
            throw new ProviderException("scope_missing", false);
        }

        return new TokenSet(root.Str("access_token") ?? throw new ProviderException("no_access_token", false), root.Str("refresh_token"), ExpiresIn(root), scopes);
    }

    public async Task<TokenSet> RefreshAsync(TokenSet current, CancellationToken cancellationToken)
    {
        if (current.RefreshToken is null)
        {
            throw new ProviderException("no_refresh_token", true);
        }

        using var json = await PostTokenAsync(
            new Dictionary<string, string> { ["refresh_token"] = current.RefreshToken, ["client_id"] = options.ClientId!, ["client_secret"] = options.ClientSecret!, ["grant_type"] = "refresh_token" },
            cancellationToken);
        return current with { AccessToken = json.RootElement.Str("access_token") ?? throw new ProviderException("no_access_token", true), ExpiresAt = ExpiresIn(json.RootElement) };
    }

    public async Task<SocialProfile> GetProfileAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"{Api}/channels?part=snippet&mine=true", tokens, cancellationToken);
        var channel = json.RootElement.Items("items").FirstOrDefault();
        if (channel.ValueKind != JsonValueKind.Object || channel.Str("id") is not { } id)
        {
            throw new ProviderException("no_channel", false, "Ce compte Google n'a pas de chaîne YouTube.");
        }

        var snippet = channel.Obj("snippet");
        return new SocialProfile(id, snippet.Str("customUrl") ?? snippet.Str("title") ?? id, IsProfessional: true);
    }

    public async Task<RemoteFetch> ListAsync(TokenSet tokens, string externalUserId, int limit, CancellationToken cancellationToken)
    {
        // The uploads playlist of a channel UCxxxx is UUxxxx.
        var uploads = externalUserId.StartsWith("UC", StringComparison.Ordinal) ? "UU" + externalUserId[2..] : externalUserId;
        List<string> ids = [];
        string? page = null;
        do
        {
            var address = $"{Api}/playlistItems?part=contentDetails&playlistId={Uri.EscapeDataString(uploads)}&maxResults=50{(page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page))}";
            using var json = await GetJsonAsync(address, tokens, cancellationToken);
            ids.AddRange(json.RootElement.Items("items").Select(item => item.Obj("contentDetails").Str("videoId")).OfType<string>());
            page = json.RootElement.Str("nextPageToken");
        }
        while (page is not null && ids.Count < limit);

        var complete = page is null;
        ids = [.. ids.Distinct(StringComparer.Ordinal).Take(limit)];
        List<RemoteContent> items = [];
        foreach (var batch in ids.Chunk(50))
        {
            using var json = await GetJsonAsync($"{Api}/videos?part=snippet,contentDetails&maxResults=50&id={Uri.EscapeDataString(string.Join(',', batch))}", tokens, cancellationToken);
            items.AddRange(json.RootElement.Items("items").Select(ToContent).OfType<RemoteContent>());
        }

        return new RemoteFetch(FetchStatus.Ok, items, complete);
    }

    public async Task RevokeAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.RefreshToken ?? tokens.AccessToken }), cancellationToken);
    }

    internal static RemoteContent? ToContent(JsonElement video)
    {
        if (video.Str("id") is not { } id)
        {
            return null;
        }

        var snippet = video.Obj("snippet");
        var duration = IsoDuration.ToSeconds(video.Obj("contentDetails").Str("duration"));
        var description = snippet.Str("description");
        var thumbnails = snippet.Obj("thumbnails");
        var thumbnail = ThumbnailSizes.Select(size => thumbnails.Obj(size).Str("url")).FirstOrDefault(url => url is not null);
        var published = DateTimeOffset.TryParse(snippet.Str("publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : (DateTimeOffset?)null;
        return new RemoteContent(id, $"https://www.youtube.com/watch?v={id}", snippet.Str("title") ?? "Sans titre", description, ContentKinds.Video, published, duration, thumbnail, ChapterParser.Parse(description, duration));
    }

    private DateTimeOffset? ExpiresIn(JsonElement root) =>
        root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt64(out var value) ? clock.GetUtcNow().AddSeconds(value) : null;

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(string address, TokenSet tokens, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>Google errors: OAuth endpoints answer <c>{"error": "invalid_grant"}</c>, the API <c>{"error": {"code": 401, ...}}</c>. A 401 or an invalid grant means the tokens are dead.</summary>
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

        if (response.IsSuccessStatusCode && json is not null)
        {
            return json;
        }

        var error = json is not null && json.RootElement.TryGetProperty("error", out var node) ? node : default;
        var oauthError = error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        var reauthorization = response.StatusCode == HttpStatusCode.Unauthorized || oauthError is "invalid_grant" or "invalid_token";
        json?.Dispose();
        throw new ProviderException(reauthorization ? "token_rejected" : $"http_{(int)response.StatusCode}", reauthorization);
    }
}

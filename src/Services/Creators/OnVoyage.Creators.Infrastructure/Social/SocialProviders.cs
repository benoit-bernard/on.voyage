using OnVoyage.Creators.Application.Ports;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>Configuration of one platform (<c>Creators:Social:Instagram</c> / <c>YouTube</c>). The secrets come from the environment, never from the repository.</summary>
internal sealed class PlatformOptions
{
    /// <summary>Off by default: the applications are under review (H-008).</summary>
    public bool Enabled { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The address the platform sends the creator back to: a page of the creator space, which hands the code to the Creators API with the creator's own token.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>Send a PKCE challenge (S256). Google supports it; Instagram's support is not documented: turn it off there if the authorization is refused.</summary>
    public bool UsePkce { get; set; } = true;

    /// <summary>Instagram only: Graph API version prefix (<c>v23.0</c>), empty for the unversioned address.</summary>
    public string? ApiVersion { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

internal sealed class SocialOptions
{
    public const string Section = "Creators:Social";

    /// <summary><c>live</c> (the platforms) or <c>fake</c> (deterministic adapters: tests and local runs without credentials).</summary>
    public string Provider { get; set; } = "live";

    public PlatformOptions Instagram { get; set; } = new();

    public PlatformOptions YouTube { get; set; } = new();

    /// <summary>Folder of the media root where thumbnails are copied (the one Catalog serves under <c>/media</c>). Empty: no thumbnail is copied.</summary>
    public string? MediaDirectory { get; set; }

    public int ThumbnailMaxBytes { get; set; } = 1_000_000;

    /// <summary>Hosts a thumbnail may be downloaded from (<c>*.</c> prefix for subdomains). Anything else is ignored: the address comes from a third party.</summary>
    public string[] ThumbnailHosts { get; set; } = ["i.ytimg.com", "img.youtube.com", "*.cdninstagram.com", "*.fbcdn.net"];

    /// <summary>Runs the daily synchronisation in the Creators API process.</summary>
    public bool SyncEnabled { get; set; }

    public int SyncIntervalHours { get; set; } = 24;
}

/// <summary>Tokens as the platform gave them. Lives in memory only inside the connector; stored encrypted.</summary>
internal sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes);

internal sealed record SocialProfile(string ExternalUserId, string Username, bool IsProfessional);

/// <summary>The platform refused the credentials of the call, or the call itself.</summary>
internal sealed class ProviderException(string code, bool reauthorizationRequired, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;

    /// <summary>The tokens are no longer accepted: nothing but a new authorization will work.</summary>
    public bool ReauthorizationRequired { get; } = reauthorizationRequired;
}

/// <summary>One social platform, as OAuth 2.0 code flow + the calls the import needs (F-27). Implementations never log a token or a URL that carries one.</summary>
internal interface ISocialProvider
{
    string Platform { get; }

    PlatformOptions Options { get; }

    bool UsesPkce { get; }

    /// <summary>How long before its expiry an access token is renewed.</summary>
    TimeSpan RefreshMargin { get; }

    Uri AuthorizeUrl(string state, string? codeChallenge);

    Task<TokenSet> ExchangeCodeAsync(string code, string? codeVerifier, CancellationToken cancellationToken);

    /// <summary>A fresh token set. Throws <see cref="ProviderException"/> with <c>ReauthorizationRequired</c> when the platform no longer accepts the refresh.</summary>
    Task<TokenSet> RefreshAsync(TokenSet current, CancellationToken cancellationToken);

    Task<SocialProfile> GetProfileAsync(TokenSet tokens, CancellationToken cancellationToken);

    Task<RemoteFetch> ListAsync(TokenSet tokens, string externalUserId, int limit, CancellationToken cancellationToken);

    Task RevokeAsync(TokenSet tokens, CancellationToken cancellationToken);
}

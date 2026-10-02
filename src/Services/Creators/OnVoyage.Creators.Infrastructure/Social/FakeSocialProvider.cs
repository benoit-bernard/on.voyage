using System.Collections.Concurrent;
using OnVoyage.Creators.Application.Ports;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>A platform account in the fake world: who it is, what it has published, and whether its tokens still work.</summary>
internal sealed class FakeAccount(string externalUserId, string username, bool isProfessional = true)
{
    public string ExternalUserId { get; } = externalUserId;

    public string Username { get; } = username;

    public bool IsProfessional { get; } = isProfessional;

    public List<RemoteContent> Contents { get; } = [];

    /// <summary>False once the platform "revoked" the access: renewals fail as a revoked grant does.</summary>
    public bool TokensValid { get; set; } = true;

    public bool Unavailable { get; set; }

    /// <summary>The list stops here as if it had more pages.</summary>
    public bool Truncated { get; set; }

    public int Revocations { get; set; }

    public int Refreshes { get; set; }
}

/// <summary>
/// The platforms, for tests and for local runs without credentials (<c>Creators:Social:Provider = fake</c>): deterministic and offline. A code is
/// <c>fake-code:&lt;user&gt;</c>; the world is a singleton that tests fill and inspect.
/// </summary>
internal sealed class FakeSocialWorld
{
    private readonly ConcurrentDictionary<string, FakeAccount> _accounts = new(StringComparer.Ordinal);

    public FakeAccount Account(string platform, string user, bool isProfessional = true) =>
        _accounts.GetOrAdd($"{platform}:{user}", _ => new FakeAccount($"{platform}-{user}", user, isProfessional));

    public FakeAccount? Find(string platform, string user) => _accounts.GetValueOrDefault($"{platform}:{user}");

    public FakeAccount? ByExternalId(string externalUserId) => _accounts.Values.FirstOrDefault(account => account.ExternalUserId == externalUserId);

    public static string CodeFor(string user) => $"fake-code:{user}";
}

internal sealed class FakeSocialProvider(string platform, PlatformOptions options, FakeSocialWorld world, TimeProvider clock) : ISocialProvider
{
    public string Platform => platform;

    public PlatformOptions Options => options;

    public bool UsesPkce => options.UsePkce;

    public TimeSpan RefreshMargin => TimeSpan.FromMinutes(5);

    public Uri AuthorizeUrl(string state, string? codeChallenge) =>
        new($"https://fake.onvoyage.test/{platform}/authorize?state={Uri.EscapeDataString(state)}{(codeChallenge is null ? string.Empty : "&code_challenge=" + codeChallenge)}");

    public Task<TokenSet> ExchangeCodeAsync(string code, string? codeVerifier, CancellationToken cancellationToken)
    {
        var user = code.StartsWith("fake-code:", StringComparison.Ordinal) ? code["fake-code:".Length..] : throw new ProviderException("invalid_grant", false);
        var account = world.Find(platform, user) ?? throw new ProviderException("invalid_grant", false);
        return Task.FromResult(new TokenSet($"access:{account.ExternalUserId}", platform == "youtube" ? $"refresh:{account.ExternalUserId}" : null, clock.GetUtcNow().AddHours(1), [platform == "youtube" ? YouTubeProvider.Scope : InstagramProvider.Scope]));
    }

    public Task<TokenSet> RefreshAsync(TokenSet current, CancellationToken cancellationToken)
    {
        var account = Of(current);
        if (!account.TokensValid)
        {
            throw new ProviderException("token_rejected", true);
        }

        account.Refreshes++;
        return Task.FromResult(current with { AccessToken = $"access:{account.ExternalUserId}:{account.Refreshes}", ExpiresAt = clock.GetUtcNow().AddHours(1) });
    }

    public Task<SocialProfile> GetProfileAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        var account = Of(tokens);
        return Task.FromResult(new SocialProfile(account.ExternalUserId, account.Username, account.IsProfessional));
    }

    public Task<RemoteFetch> ListAsync(TokenSet tokens, string externalUserId, int limit, CancellationToken cancellationToken)
    {
        var account = Of(tokens);
        if (!account.TokensValid)
        {
            throw new ProviderException("token_rejected", true);
        }

        if (account.Unavailable)
        {
            throw new ProviderException("http_503", false);
        }

        var items = account.Contents.Take(limit).ToList();
        return Task.FromResult(new RemoteFetch(FetchStatus.Ok, items, !account.Truncated && account.Contents.Count <= limit));
    }

    public Task RevokeAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        Of(tokens).Revocations++;
        return Task.CompletedTask;
    }

    private FakeAccount Of(TokenSet tokens)
    {
        var id = tokens.AccessToken.Split(':')[1];
        return world.ByExternalId(id) ?? throw new ProviderException("token_rejected", true);
    }
}

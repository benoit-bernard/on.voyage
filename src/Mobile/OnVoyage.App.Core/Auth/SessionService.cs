using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Auth;

/// <summary>
/// Owns the device session (F-01): creates the anonymous one, refreshes it before it expires, and links or restores an account through
/// the e-mail code. One refresh at a time: the refresh token is single-use, so concurrent refreshes would revoke each other.
/// </summary>
public sealed class SessionService(IAuthClient client, ISessionStore store, TimeProvider clock) : ISessionProvider, IDisposable
{
    /// <summary>Refresh when less than this remains of the access token.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuthSessionDto? _current;
    private bool _forceRefresh;

    public event Action<AuthSessionDto>? SessionChanged;

    public void Dispose() => _gate.Dispose();

    public async Task<AuthSessionDto?> PeekAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _current ??= await store.LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateAccessToken() => _forceRefresh = true;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        (await EnsureSessionAsync(cancellationToken)).AccessToken;

    public async Task<AuthSessionDto> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = _current ??= await store.LoadAsync(cancellationToken);
            var now = clock.GetUtcNow();

            if (session is null)
            {
                return await CreateAnonymousAsync(cancellationToken);
            }

            var stillGood = !_forceRefresh && session.AccessTokenExpiresAt - now > RefreshMargin;
            if (stillGood)
            {
                return session;
            }

            var refreshed = await client.RefreshAsync(session.RefreshToken, cancellationToken);
            if (refreshed.IsSuccess)
            {
                _forceRefresh = false;
                return await AdoptAsync(refreshed.Value!, cancellationToken);
            }

            if (refreshed.Failure!.Code == AuthFailure.InvalidRefreshToken)
            {
                // The server no longer knows this session (revoked, expired, account replaced): start over anonymously.
                await store.ClearAsync(cancellationToken);
                _current = null;
                _forceRefresh = false;
                return await CreateAnonymousAsync(cancellationToken);
            }

            // Network trouble: keep working with the access token while it is still valid.
            if (session.AccessTokenExpiresAt > now)
            {
                return session;
            }

            throw new HttpRequestException($"Cannot refresh the session: {refreshed.Failure.Code}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuthResult<bool>> RequestCodeAsync(string email, CancellationToken cancellationToken)
    {
        var session = await EnsureSessionAsync(cancellationToken);
        return await client.RequestCodeAsync(email, session.AccessToken, cancellationToken);
    }

    /// <summary>Verifies the code. On success the session becomes the verified one: same traveler id when linking, the existing account's when restoring.</summary>
    public async Task<AuthResult<AuthSessionDto>> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var session = await EnsureSessionAsync(cancellationToken);
        var result = await client.VerifyCodeAsync(email, code, session.AccessToken, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AdoptAsync(result.Value!, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    /// <summary>Signs out: revokes the refresh token and falls back to a brand-new anonymous session.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var session = await PeekAsync(cancellationToken);
        if (session is not null)
        {
            try
            {
                await client.SignOutAsync(session.RefreshToken, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Offline sign-out still clears the device; the server-side token expires on its own.
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await store.ClearAsync(cancellationToken);
            _current = null;
        }
        finally
        {
            _gate.Release();
        }

        await EnsureSessionAsync(cancellationToken);
    }

    private async Task<AuthSessionDto> CreateAnonymousAsync(CancellationToken cancellationToken)
    {
        var created = await client.StartAnonymousAsync(cancellationToken);
        return created.IsSuccess
            ? await AdoptAsync(created.Value!, cancellationToken)
            : throw new HttpRequestException($"Cannot start a session: {created.Failure!.Code}.");
    }

    private async Task<AuthSessionDto> AdoptAsync(AuthSessionDto session, CancellationToken cancellationToken)
    {
        _current = session;
        await store.SaveAsync(session, cancellationToken);
        SessionChanged?.Invoke(session);
        return session;
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using OnVoyage.Platform.Contracts;
using OnVoyage.Web.Studio.Api;

namespace OnVoyage.Web.Studio.Auth;

public static class StudioClaims
{
    public const string SessionId = "sid";
    public const string Roles = "roles";
    public const string CreatorRole = "creator";
    public const string PolicyName = "creator";

    public static bool IsCreator(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.HasClaim(Roles, CreatorRole);
}

/// <summary>
/// The creator's Platform tokens, kept on the server (never in the cookie, never in the browser). The cookie only carries an opaque session
/// id; the roles of the principal are read from here at every request, so the <c>creator</c> role shows up as soon as Platform has granted it.
/// A restart signs everyone out.
/// </summary>
public sealed class StudioTokenStore
{
    private readonly ConcurrentDictionary<string, AuthSessionDto> _sessions = new(StringComparer.Ordinal);

    public string Add(AuthSessionDto session)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        _sessions[id] = session;
        return id;
    }

    public AuthSessionDto? Find(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public void Update(string id, AuthSessionDto session) => _sessions[id] = session;

    public void Remove(string id) => _sessions.TryRemove(id, out _);
}

/// <summary>Calls Platform's public sign-in endpoints through the Gateway.</summary>
public sealed class PlatformAuthClient(IHttpClientFactory clients)
{
    private const string Base = "api/platform/v1/auth";

    public Task RequestCodeAsync(string email, CancellationToken cancellationToken) => PostAsync($"{Base}/otp/request", new RequestOtpRequest(email), cancellationToken);

    public async Task<AuthSessionDto> VerifyAsync(string email, string code, CancellationToken cancellationToken)
    {
        using var response = await PostAsync($"{Base}/otp/verify", new VerifyOtpRequest(email, code), cancellationToken);
        return await response.Content.ReadFromJsonAsync<AuthSessionDto>(StudioJson.Options, cancellationToken) ?? throw new StudioApiException("Réponse vide.", 502);
    }

    public async Task<AuthSessionDto> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await PostAsync($"{Base}/refresh", new RefreshSessionRequest(refreshToken), cancellationToken);
        return await response.Content.ReadFromJsonAsync<AuthSessionDto>(StudioJson.Options, cancellationToken) ?? throw new StudioApiException("Réponse vide.", 502);
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync($"{Base}/signout", new RefreshSessionRequest(refreshToken), cancellationToken);
        }
        catch (StudioApiException)
        {
            // The local session ends either way.
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        var response = await clients.CreateClient(GatewayCaller.ClientName).PostAsJsonAsync(path, body, StudioJson.Options, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var title = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "Trop d'essais : patientez un instant." : "Cela n'a pas fonctionné.";
        try
        {
            if ((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).TryGetProperty("title", out var value) && value.GetString() is { Length: > 0 } text)
            {
                title = text;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Keep the generic message.
        }

        response.Dispose();
        throw new StudioApiException(title, (int)response.StatusCode);
    }
}

/// <summary>Brings the roles of the session up to date with Platform's, which grants <c>creator</c> a moment after the creator terms were accepted.</summary>
public interface IRoleRefresher
{
    /// <summary>Asks Platform for a fresh session until it carries the <c>creator</c> role, a few times at most. False when the role has not arrived yet.</summary>
    Task<bool> WaitForCreatorRoleAsync(CancellationToken cancellationToken);
}

/// <summary>Per-circuit view of the signed-in creator: hands out a valid access token, refreshing it when it is about to expire.</summary>
public sealed class StudioSession(AuthenticationStateProvider authentication, StudioTokenStore store, PlatformAuthClient platform, TimeProvider clock) : IRoleRefresher
{
    private const int Attempts = 6;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var id = await SessionIdAsync();
        if (id is null || store.Find(id) is not { } session)
        {
            return null;
        }

        if (session.AccessTokenExpiresAt - TimeSpan.FromSeconds(30) > clock.GetUtcNow())
        {
            return session.AccessToken;
        }

        return (await RefreshAsync(id, force: false, cancellationToken))?.AccessToken;
    }

    public async Task<bool> WaitForCreatorRoleAsync(CancellationToken cancellationToken)
    {
        var id = await SessionIdAsync();
        for (var attempt = 0; id is not null && attempt < Attempts; attempt++)
        {
            if (await RefreshAsync(id, force: true, cancellationToken) is { } session && session.Roles.Contains(StudioClaims.CreatorRole))
            {
                return true;
            }

            await Task.Delay(Pause, clock, cancellationToken);
        }

        return false;
    }

    private async Task<string?> SessionIdAsync() => (await authentication.GetAuthenticationStateAsync()).User.FindFirstValue(StudioClaims.SessionId);

    private async Task<AuthSessionDto?> RefreshAsync(string id, bool force, CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another circuit of the same creator may have refreshed while we waited: refresh tokens are single use.
            if (store.Find(id) is not { } session)
            {
                return null;
            }

            if (!force && session.AccessTokenExpiresAt - TimeSpan.FromSeconds(30) > clock.GetUtcNow())
            {
                return session;
            }

            try
            {
                var renewed = await platform.RefreshAsync(session.RefreshToken, cancellationToken);
                store.Update(id, renewed);
                return renewed;
            }
            catch (StudioApiException)
            {
                store.Remove(id);
                return null;
            }
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}

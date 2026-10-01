using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using OnVoyage.Platform.Contracts;
using OnVoyage.Web.Admin.Api;

namespace OnVoyage.Web.Admin.Auth;

public static class AdminClaims
{
    public const string SessionId = "sid";
    public const string Roles = "roles";
    public const string AdminRole = "admin";
    public const string PolicyName = "admin";

    public static bool IsAdmin(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.HasClaim(Roles, AdminRole);
}

/// <summary>
/// The editor's Platform tokens, kept on the server (never in the cookie, never in the browser). The cookie only carries an opaque
/// session id. A restart signs everyone out, which is acceptable for a back-office with a handful of users.
/// </summary>
public sealed class AdminTokenStore
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

    public Task RequestCodeAsync(string email, CancellationToken cancellationToken) => PostAsync(email, $"{Base}/otp/request", new RequestOtpRequest(email), cancellationToken);

    public async Task<AuthSessionDto> VerifyAsync(string email, string code, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(email, $"{Base}/otp/verify", new VerifyOtpRequest(email, code), cancellationToken);
        return await response.Content.ReadFromJsonAsync<AuthSessionDto>(AdminJson.Options, cancellationToken) ?? throw new AdminApiException("Empty response.", 502);
    }

    public async Task<AuthSessionDto> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(null, $"{Base}/refresh", new RefreshSessionRequest(refreshToken), cancellationToken);
        return await response.Content.ReadFromJsonAsync<AuthSessionDto>(AdminJson.Options, cancellationToken) ?? throw new AdminApiException("Empty response.", 502);
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync(null, $"{Base}/signout", new RefreshSessionRequest(refreshToken), cancellationToken);
        }
        catch (AdminApiException)
        {
            // The local session ends either way.
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string? _, string path, object body, CancellationToken cancellationToken)
    {
        var response = await clients.CreateClient(HttpAdminApi.ClientName).PostAsJsonAsync(path, body, AdminJson.Options, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var title = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "Too many attempts: wait a moment." : "That did not work.";
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
        throw new AdminApiException(title, (int)response.StatusCode);
    }
}

/// <summary>Per-circuit view of the signed-in editor: hands out a valid access token, refreshing it when it is about to expire.</summary>
public sealed class AdminSession(AuthenticationStateProvider authentication, AdminTokenStore store, PlatformAuthClient platform, TimeProvider clock)
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        var id = state.User.FindFirstValue(AdminClaims.SessionId);
        if (id is null || store.Find(id) is not { } session)
        {
            return null;
        }

        if (session.AccessTokenExpiresAt - TimeSpan.FromSeconds(30) > clock.GetUtcNow())
        {
            return session.AccessToken;
        }

        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another circuit of the same editor may have refreshed while we waited: refresh tokens are single use.
            session = store.Find(id);
            if (session is null)
            {
                return null;
            }

            if (session.AccessTokenExpiresAt - TimeSpan.FromSeconds(30) > clock.GetUtcNow())
            {
                return session.AccessToken;
            }

            try
            {
                var renewed = await platform.RefreshAsync(session.RefreshToken, cancellationToken);
                store.Update(id, renewed);
                return renewed.AccessToken;
            }
            catch (AdminApiException)
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

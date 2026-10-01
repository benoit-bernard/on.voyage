using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.App.Core.Auth;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Infrastructure.Auth;

/// <summary>Identity API client. It has no bearer handler: it is what the handler itself uses to obtain and refresh tokens.</summary>
internal sealed class HttpAuthClient(HttpClient http) : IAuthClient
{
    private const string Base = "api/platform/v1/auth/";

    public Task<AuthResult<AuthSessionDto>> StartAnonymousAsync(CancellationToken cancellationToken) =>
        SendAsync<AuthSessionDto>("anonymous", null, null, cancellationToken);

    public Task<AuthResult<AuthSessionDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        SendAsync<AuthSessionDto>("refresh", new RefreshSessionRequest(refreshToken), null, cancellationToken);

    public async Task<AuthResult<bool>> RequestCodeAsync(string email, string? accessToken, CancellationToken cancellationToken)
    {
        var result = await SendAsync<JsonElement?>("otp/request", new RequestOtpRequest(email), accessToken, cancellationToken);
        return result.IsSuccess ? AuthResult.Success(true) : AuthResult.Fail<bool>(result.Failure!);
    }

    public Task<AuthResult<AuthSessionDto>> VerifyCodeAsync(string email, string code, string? accessToken, CancellationToken cancellationToken) =>
        SendAsync<AuthSessionDto>("otp/verify", new VerifyOtpRequest(email, code), accessToken, cancellationToken);

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken) =>
        await SendAsync<JsonElement?>("signout", new RefreshSessionRequest(refreshToken), null, cancellationToken);

    private async Task<AuthResult<T>> SendAsync<T>(string path, object? body, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
                {
                    return AuthResult.Success<T>(default!);
                }

                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return value is null ? AuthResult.Fail<T>(new AuthFailure(AuthFailure.Network, "Empty response.")) : AuthResult.Success(value);
            }

            return AuthResult.Fail<T>(await ReadFailureAsync(response, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Fail<T>(new AuthFailure(AuthFailure.Network, "The server cannot be reached."));
        }
    }

    private static async Task<AuthFailure> ReadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var title = document.RootElement.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var code = type is not null && type.StartsWith("https://on.voyage/problems/", StringComparison.Ordinal) ? type["https://on.voyage/problems/".Length..] : $"http_{(int)response.StatusCode}";
            return new AuthFailure(code, title ?? "Request failed.", retryAfter);
        }
        catch (JsonException)
        {
            return new AuthFailure($"http_{(int)response.StatusCode}", "Request failed.", retryAfter);
        }
    }
}

/// <summary>Adds the bearer token and app version to API calls, and retries once with a refreshed token after a 401.</summary>
internal sealed class BearerTokenHandler(ISessionProvider sessions, AppVersion appVersion) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The request can only be sent once, so a retry needs a copy taken before the first send.
        using var retry = await CloneAsync(request, cancellationToken);

        Prepare(request, await sessions.GetAccessTokenAsync(cancellationToken));
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        sessions.InvalidateAccessToken();
        Prepare(retry, await sessions.GetAccessTokenAsync(cancellationToken));
        return await base.SendAsync(retry, cancellationToken);
    }

    private void Prepare(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-App-Version");
        request.Headers.TryAddWithoutValidation("X-App-Version", appVersion.Value);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}

/// <summary>The running app's version, sent as <c>X-App-Version</c> so the server can ask for an update (426).</summary>
public sealed record AppVersion(string Value);

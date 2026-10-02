using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Web.Studio.Auth;

namespace OnVoyage.Web.Studio.Api;

/// <summary>
/// The calls of the creator space to the Gateway, with the creator's own token. A refusal becomes a <see cref="StudioApiException"/> carrying the
/// <c>title</c> of the Problem Details, which the page shows as it is.
/// </summary>
internal sealed class GatewayCaller(IHttpClientFactory clients, StudioSession session)
{
    public const string ClientName = "gateway";

    private static JsonSerializerOptions Json => StudioJson.Options;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await session.GetAccessTokenAsync(cancellationToken) ?? throw new StudioApiException("Votre session a expiré : reconnectez-vous.", 401);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string title;
        try
        {
            title = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).TryGetProperty("title", out var value) ? value.GetString() ?? response.ReasonPhrase ?? "Erreur" : response.ReasonPhrase ?? "Erreur";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            title = response.ReasonPhrase ?? "Erreur";
        }

        response.Dispose();
        throw new StudioApiException(title, (int)response.StatusCode);
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new StudioApiException("Réponse vide.", 502);
    }

    public async Task<T> WriteAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body ?? new { }, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new StudioApiException("Réponse vide.", 502);
    }

    public async Task WriteAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body ?? new { }, cancellationToken);
    }
}

using System.Text.Json;
using Microsoft.JSInterop;
using OnVoyage.App.Core.Auth;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Web.Pwa;

/// <summary>
/// Keeps the session in the browser's localStorage. Like any web app that stores tokens in script-readable storage it is exposed
/// to XSS, which is why the PWA ships no third-party script and a strict CSP; refresh tokens are single-use and rotated.
/// </summary>
internal sealed class BrowserSessionStore(IJSRuntime js) : ISessionStore
{
    private const string Key = "onvoyage.session.v1";

    public async Task<AuthSessionDto?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, Key);
            return json is null ? null : JsonSerializer.Deserialize<AuthSessionDto>(json);
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(AuthSessionDto session, CancellationToken cancellationToken)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", cancellationToken, Key, JsonSerializer.Serialize(session));
        }
        catch (JSException)
        {
            // Blocked storage: the session lives for this page load only.
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", cancellationToken, Key);
        }
        catch (JSException)
        {
        }
    }
}

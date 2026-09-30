using System.Text.Json;
using Microsoft.JSInterop;
using OnVoyage.App.Core.Profile;

namespace OnVoyage.Web.Pwa;

/// <summary>Keeps the taste profile in the browser's localStorage. It never leaves the device.</summary>
internal sealed class BrowserProfileStore(IJSRuntime js) : IProfileStore
{
    private const string Key = "onvoyage.profile.v1";
    private LocalProfile? _cached;

    public async Task<LocalProfile> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, Key);
            _cached = json is null ? new LocalProfile() : JsonSerializer.Deserialize<LocalProfile>(json) ?? new LocalProfile();
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            _cached = new LocalProfile();
        }

        return _cached;
    }

    public async Task SaveAsync(LocalProfile profile, CancellationToken cancellationToken)
    {
        _cached = profile;
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", cancellationToken, Key, JsonSerializer.Serialize(profile));
        }
        catch (JSException)
        {
            // Private mode or blocked storage: the profile stays in memory for this session.
        }
    }
}

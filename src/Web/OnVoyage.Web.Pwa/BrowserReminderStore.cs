using System.Text.Json;
using Microsoft.JSInterop;
using OnVoyage.App.Core.Wishes;

namespace OnVoyage.Web.Pwa;

/// <summary>Last reminder dates in the browser's local storage: on this device only, never sent anywhere (F-08).</summary>
internal sealed class BrowserReminderStore(IJSRuntime js) : IReminderStore
{
    private const string Key = "onvoyage.reminders.v1";
    private Dictionary<Guid, DateTimeOffset>? _cached;

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, Key);
            _cached = json is null ? [] : JsonSerializer.Deserialize<Dictionary<Guid, DateTimeOffset>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            _cached = [];
        }

        return _cached;
    }

    public async Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _ = await LoadAsync(cancellationToken);
        _cached![poiId] = at;
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", cancellationToken, Key, JsonSerializer.Serialize(_cached));
        }
        catch (JSException)
        {
            // Private browsing may refuse storage; the reminder still showed.
        }
    }
}

using Microsoft.JSInterop;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.UI.Components.Device;

/// <summary>
/// Position and wake lock in the browser, so discovery mode works in the PWA while the page is open. Browsers stop sending positions when
/// the screen locks: the PWA is a foreground experience, the phone apps carry the background mode (MVP, T-612).
/// </summary>
public sealed class BrowserLocation(IJSRuntime js) : ILocationProvider, ILocationSource, IAsyncDisposable
{
    private const string Module = "./_content/OnVoyage.UI.Components/js/device.js";
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserLocation>? _reference;
    private bool _watching;

    public event Action<LocationFix>? FixReceived;

    public async Task<Position?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        var found = await module.InvokeAsync<PositionDto?>("currentPosition", cancellationToken);
        return found is null ? null : new Position(found.Latitude, found.Longitude);
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (_watching)
        {
            return true;
        }

        var module = await ModuleAsync(cancellationToken);
        _reference ??= DotNetObjectReference.Create(this);
        _watching = await module.InvokeAsync<bool>("startWatch", cancellationToken, _reference);
        return _watching;
    }

    public async Task StopAsync()
    {
        if (_module is not null && _watching)
        {
            await _module.InvokeVoidAsync("stopWatch");
        }

        _watching = false;
    }

    [JSInvokable]
    public void OnFix(double latitude, double longitude, double accuracy, double? speed, double? heading, double timestampMs) =>
        FixReceived?.Invoke(new LocationFix(latitude, longitude, accuracy, speed, heading, DateTimeOffset.FromUnixTimeMilliseconds((long)timestampMs)));

    public async Task SetAwakeAsync(bool awake) => await (await ModuleAsync(CancellationToken.None)).InvokeVoidAsync("setAwake", awake);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }

        _reference?.Dispose();
    }

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken) =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, Module);

    private sealed record PositionDto(double Latitude, double Longitude);
}

public sealed class BrowserKeepAwake(BrowserLocation device) : IScreenKeepAwake
{
    public void SetAwake(bool awake) => _ = device.SetAwakeAsync(awake);
}

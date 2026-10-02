using OnVoyage.App.Core.Background;

namespace OnVoyage.App;

/// <summary>
/// <see cref="ILocationPermissions"/> over MAUI Essentials (both phones). The system dialogs must be raised from the main thread. On Android 11
/// and later, "Always" is not a dialog but a trip to the system settings screen of the app; Essentials returns when the traveler comes back.
/// Not compiled in the repository's own CI image: see docs/MOBILE.md.
/// </summary>
internal sealed class MauiLocationPermissions : ILocationPermissions
{
    public Task<LocationPermissionLevel> CheckAsync(CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            {
                return LocationPermissionLevel.Denied;
            }

            return await Permissions.CheckStatusAsync<Permissions.LocationAlways>() == PermissionStatus.Granted ? LocationPermissionLevel.Always : LocationPermissionLevel.WhenInUse;
        });

    public Task<LocationPermissionLevel> RequestWhenInUseAsync(CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted ? LocationPermissionLevel.WhenInUse : LocationPermissionLevel.Denied);

    public Task<LocationPermissionLevel> RequestAlwaysAsync(CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (await Permissions.RequestAsync<Permissions.LocationAlways>() == PermissionStatus.Granted)
            {
                return LocationPermissionLevel.Always;
            }

            // Refusing "Always" must not take the foreground permission away: report what is still granted.
            return await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted ? LocationPermissionLevel.WhenInUse : LocationPermissionLevel.Denied;
        });
}

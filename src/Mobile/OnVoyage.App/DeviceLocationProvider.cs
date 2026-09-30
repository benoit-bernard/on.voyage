using OnVoyage.App.Core;

namespace OnVoyage.App;

/// <summary>Foreground GPS through MAUI <c>Geolocation</c> (MVP-0). Returns <c>null</c> when permission is denied or unavailable.</summary>
internal sealed class DeviceLocationProvider : ILocationProvider
{
    public async Task<Position?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (status != PermissionStatus.Granted)
            {
                return null;
            }

            var location = await Geolocation.Default.GetLastKnownLocationAsync()
                ?? await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(8)), cancellationToken);
            return location is null ? null : new Position(location.Latitude, location.Longitude);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException or PermissionException)
        {
            return null;
        }
    }
}

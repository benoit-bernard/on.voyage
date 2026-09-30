using System.Text.Json;
using OnVoyage.App.Core.Profile;

namespace OnVoyage.App;

/// <summary>Stores the taste profile in the platform preferences (device only). SQLite arrives with the offline packs (MVP).</summary>
internal sealed class PreferencesProfileStore : IProfileStore
{
    private const string Key = "onvoyage.profile.v1";
    private LocalProfile? _cached;

    public Task<LocalProfile> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return Task.FromResult(_cached);
        }

        try
        {
            var json = Preferences.Default.Get<string?>(Key, null);
            _cached = json is null ? new LocalProfile() : JsonSerializer.Deserialize<LocalProfile>(json) ?? new LocalProfile();
        }
        catch (JsonException)
        {
            _cached = new LocalProfile();
        }

        return Task.FromResult(_cached);
    }

    public Task SaveAsync(LocalProfile profile, CancellationToken cancellationToken)
    {
        _cached = profile;
        Preferences.Default.Set(Key, JsonSerializer.Serialize(profile));
        return Task.CompletedTask;
    }
}

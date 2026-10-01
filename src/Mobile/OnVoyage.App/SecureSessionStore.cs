using System.Text.Json;
using OnVoyage.App.Core.Auth;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App;

/// <summary>Session tokens in the platform secure storage (Android Keystore, iOS Keychain), as required by §14.2.</summary>
internal sealed class SecureSessionStore : ISessionStore
{
    private const string Key = "onvoyage.session.v1";

    public async Task<AuthSessionDto?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? null : JsonSerializer.Deserialize<AuthSessionDto>(json);
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            // Keystore invalidated (restore on another device, reinstall): behave like a first launch.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(AuthSessionDto session, CancellationToken cancellationToken) =>
        SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(session));

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}

using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Infrastructure;
using OnVoyage.App.LocalData;

namespace OnVoyage.App;

public static class MauiProgram
{
    // Gateway address per build configuration. Android emulators reach the host machine through 10.0.2.2.
    private static readonly Uri GatewayAddress = new(
#if DEBUG && ANDROID
        "http://10.0.2.2:5080/"
#elif DEBUG
        "http://localhost:5080/"
#else
        "https://api.on.voyage/"
#endif
    );

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseMauiCommunityToolkit().UseMauiCommunityToolkitMediaElement();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddAppCore();
        builder.Services.AddAppInfrastructure(GatewayAddress, AppInfo.Current.VersionString);
        builder.Services.AddSingleton<ISessionStore, SecureSessionStore>();
        builder.Services.AddSingleton<IProfileStore, PreferencesProfileStore>();
        builder.Services.AddSingleton<ILocationProvider, DeviceLocationProvider>();
        builder.Services.AddSingleton<MediaElementAudioPlayer>();
        builder.Services.AddSingleton<IAudioPlayer>(provider => provider.GetRequiredService<MediaElementAudioPlayer>());
        builder.Services.AddSingleton<ILocationSource, MauiLocationSource>();
        builder.Services.AddSingleton<IScreenKeepAwake, MauiKeepAwake>();
        builder.Services.AddLocalData(Path.Combine(FileSystem.AppDataDirectory, "user.db"));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        var app = builder.Build();
        app.Services.InitializeLocalDataAsync().GetAwaiter().GetResult();
        app.Services.GetRequiredService<OnVoyage.App.LocalData.Sync.SyncScheduler>().Start();
        return app;
    }
}

using Microsoft.Extensions.Logging;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Infrastructure;

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
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddAppCore();
        builder.Services.AddAppInfrastructure(GatewayAddress, AppInfo.Current.VersionString);
        builder.Services.AddSingleton<ISessionStore, SecureSessionStore>();
        builder.Services.AddSingleton<IProfileStore, PreferencesProfileStore>();
        builder.Services.AddSingleton<ILocationProvider, DeviceLocationProvider>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}

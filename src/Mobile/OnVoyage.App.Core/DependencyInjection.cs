using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Home;

namespace OnVoyage.App.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddAppCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionService>();
        services.AddSingleton<ISessionProvider>(provider => provider.GetRequiredService<SessionService>());
        services.AddScoped<HomeFeedService>();
        services.TryAddSingleton<IAnalyticsSink, NullAnalyticsSink>();
        services.TryAddSingleton<IFlagStore, InMemoryFlagStore>();
        services.AddSingleton<AudioPlaybackController>();
        services.TryAddSingleton<ITellHistoryStore, InMemoryTellHistoryStore>();
        services.TryAddSingleton<IVisitSink, NullVisitSink>();
        services.TryAddSingleton<IScreenKeepAwake, NoScreenKeepAwake>();
        services.TryAddSingleton<ICallMonitor, NoCallMonitor>();
        services.TryAddSingleton<ITriggerSettingsProvider, DefaultTriggerSettingsProvider>();
        services.AddScoped<DiscoveryModeController>();
        return services;
    }
}

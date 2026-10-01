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
        services.AddScoped<OnVoyage.App.Core.Planning.DestinationService>();
        services.TryAddSingleton<OnVoyage.App.Core.Interactions.IInteractionOutbox, OnVoyage.App.Core.Interactions.DirectInteractionOutbox>();
        services.AddSingleton<OnVoyage.App.Core.Interactions.InteractionSender>();
        services.AddSingleton<OnVoyage.App.Core.Interactions.InteractionRecorder>();
        services.TryAddSingleton<OnVoyage.App.Core.Feedback.ILocalNotifier, OnVoyage.App.Core.Feedback.NullLocalNotifier>();
        services.AddSingleton<OnVoyage.App.Core.Feedback.FeedbackTracker>();
        services.TryAddSingleton<OnVoyage.App.Core.Wishes.IReminderStore, OnVoyage.App.Core.Wishes.InMemoryReminderStore>();
        services.AddSingleton<OnVoyage.App.Core.Wishes.ProximityReminderService>();
        services.TryAddSingleton(new OnVoyage.App.Core.Map.MapSettings());
        return services;
    }
}

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
        services.TryAddSingleton<OnVoyage.App.Core.Analytics.AnalyticsConsent>();
        services.TryAddSingleton<OnVoyage.App.Core.Analytics.IAnalyticsTransport, OnVoyage.App.Core.Analytics.NullAnalyticsTransport>();
        services.TryAddSingleton<OnVoyage.App.Core.Analytics.IAnalyticsStore, OnVoyage.App.Core.Analytics.InMemoryAnalyticsStore>();
        services.TryAddSingleton(OnVoyage.App.Core.Analytics.AnalyticsContext.Detect("0.0.0"));
        services.TryAddSingleton<OnVoyage.App.Core.Analytics.AnalyticsQueue>();
        services.TryAddSingleton<IAnalyticsSink>(provider => provider.GetRequiredService<OnVoyage.App.Core.Analytics.AnalyticsQueue>());
        services.TryAddSingleton<IFlagStore, InMemoryFlagStore>();
        services.AddSingleton<AudioPlaybackController>();
        services.TryAddSingleton<ITellHistoryStore, InMemoryTellHistoryStore>();
        services.TryAddSingleton<IVisitSink, NullVisitSink>();
        services.TryAddSingleton<IScreenKeepAwake, NoScreenKeepAwake>();
        services.TryAddSingleton<ICallMonitor, NoCallMonitor>();
        services.TryAddSingleton<ITriggerSettingsProvider, DefaultTriggerSettingsProvider>();
        services.AddScoped<DiscoveryModeController>();
        services.AddScoped<OnVoyage.App.Core.Planning.DestinationService>();
        services.AddScoped<OnVoyage.App.Core.Onboarding.OnboardingService>();
        services.TryAddSingleton<OnVoyage.App.Core.Interactions.IInteractionOutbox, OnVoyage.App.Core.Interactions.DirectInteractionOutbox>();
        services.AddSingleton<OnVoyage.App.Core.Interactions.InteractionSender>();
        services.AddSingleton<OnVoyage.App.Core.Interactions.InteractionRecorder>();
        services.TryAddSingleton<OnVoyage.App.Core.Feedback.ILocalNotifier, OnVoyage.App.Core.Feedback.NullLocalNotifier>();
        services.AddSingleton<OnVoyage.App.Core.Feedback.FeedbackTracker>();
        services.TryAddSingleton<OnVoyage.App.Core.Wishes.IReminderStore, OnVoyage.App.Core.Wishes.InMemoryReminderStore>();
        services.AddSingleton<OnVoyage.App.Core.Wishes.ProximityReminderService>();
        services.TryAddSingleton(new OnVoyage.App.Core.Map.MapSettings());
        services.TryAddSingleton(new OnVoyage.App.Core.Creators.MediaLocator("/media"));
        services.AddScoped<OnVoyage.App.Core.Creators.CreatorsService>();
        return services;
    }
}

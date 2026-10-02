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
        services.TryAddSingleton<ITextNarrator, NullTextNarrator>(); // the PWA and the phone apps register the voice of their platform
        services.AddSingleton<AudioPlaybackController>();
        services.TryAddSingleton<ITellHistoryStore, InMemoryTellHistoryStore>();
        services.TryAddSingleton<IVisitSink, NullVisitSink>();
        services.TryAddSingleton<IScreenKeepAwake, NoScreenKeepAwake>();
        services.TryAddSingleton<ICallMonitor, NoCallMonitor>();
        services.TryAddSingleton<ITriggerSettingsProvider, DefaultTriggerSettingsProvider>();
        services.AddScoped<DiscoveryModeController>();
        services.TryAddSingleton<OnVoyage.App.Core.Background.BackgroundRationaleBroker>();
        services.TryAddSingleton<OnVoyage.App.Core.Driving.CarModeState>();
        services.AddScoped<OnVoyage.App.Core.Driving.CarModeController>();
        services.AddScoped<OnVoyage.App.Core.Planning.DestinationService>();
        services.AddScoped<OnVoyage.App.Core.Onboarding.OnboardingService>();
        services.AddScoped<OnVoyage.App.Core.Surprise.SurpriseService>();
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

    /// <summary>
    /// The phone apps' position source with the background mode (T-612). The host registers <see cref="Background.IPlatformLocationUpdates"/>,
    /// <see cref="Background.ILocationPermissions"/>, <see cref="Background.IBackgroundSession"/> and, where it exists,
    /// <see cref="Background.IBatteryOptimization"/>.
    /// </summary>
    public static IServiceCollection AddBackgroundLocation(this IServiceCollection services)
    {
        services.TryAddSingleton<Background.IBackgroundRationale>(provider => provider.GetRequiredService<Background.BackgroundRationaleBroker>());
        services.TryAddSingleton<Background.IBatteryOptimization, Background.NoBatteryOptimization>();
        services.AddSingleton<Background.BackgroundAccessFlow>();
        services.AddSingleton<Background.BackgroundLocationSource>();
        services.AddSingleton<ILocationSource>(provider => provider.GetRequiredService<Background.BackgroundLocationSource>());
        services.AddSingleton<Background.IBackgroundStatus>(provider => provider.GetRequiredService<Background.BackgroundLocationSource>());
        return services;
    }
}

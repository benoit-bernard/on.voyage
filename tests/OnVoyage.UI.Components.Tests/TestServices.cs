using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Audio;
using NSubstitute;
using OnVoyage.App.Core.Feedback;
using OnVoyage.Discovery.Contracts;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;

namespace OnVoyage.UI.Components.Tests;

internal static class TestServices
{
    /// <summary>The services the pages need to record interactions and show feedback, with an outbox that keeps what it is given.</summary>
    public static void AddLearning(this BunitContext context, IProfileStore profiles)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.AddSingleton<IInteractionOutbox>(new KeepingOutbox());
        context.Services.AddSingleton(new AudioPlaybackController(new SilentPlayer(), new NullAnalyticsSink(), new InMemoryFlagStore(), clock));
        context.Services.AddSingleton<ILocalNotifier, NullLocalNotifier>();
        context.Services.AddSingleton(new InteractionRecorder(profiles, new KeepingOutbox(), clock));
        context.Services.AddSingleton<FeedbackTracker>();
        var discovery = Substitute.For<OnVoyage.App.Core.Interactions.IDiscoveryClient>();
        discovery.GetOnboardingClipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OnboardingClipDto>>([]));
        context.Services.AddSingleton(discovery);
        context.Services.AddSingleton(new OnVoyage.App.Core.Interactions.InteractionSender(discovery, profiles));
        context.Services.AddScoped<OnVoyage.App.Core.Onboarding.OnboardingService>();
    }

    private sealed class KeepingOutbox : IInteractionOutbox
    {
        public Task EnqueueAsync(OnVoyage.Discovery.Contracts.InteractionDto interaction, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

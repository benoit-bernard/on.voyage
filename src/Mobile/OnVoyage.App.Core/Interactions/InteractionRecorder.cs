using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.App.Core.Interactions;

/// <summary>
/// Turns something the traveler did into an interaction (F-07): the local taste profile moves at once (same rule as the server, so the home
/// screen reacts immediately), the event gets its <c>client_event_id</c> and goes to the outbox. Coordinates never appear in an interaction.
/// </summary>
public sealed class InteractionRecorder(IProfileStore profiles, IInteractionOutbox outbox, TimeProvider clock)
{
    /// <summary>An interaction about a place. <paramref name="weights"/> is the place's vector, needed to move the local profile.</summary>
    public async Task<InteractionDto> RecordAsync(
        string kind,
        Guid poiId,
        IReadOnlyDictionary<string, double> weights,
        Guid? storyId = null,
        string? categoryCode = null,
        double? confidence = null,
        int? dwellSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var dto = new InteractionDto(
            Guid.NewGuid(), kind, poiId, clock.GetUtcNow(), StoryId: storyId, CategoryCode: categoryCode, Confidence: confidence, DwellS: dwellSeconds);
        await LearnLocallyAsync(dto, weights, cancellationToken);
        await outbox.EnqueueAsync(dto, cancellationToken);
        return dto;
    }

    /// <summary>A choice of the onboarding categories screen: sent to the server, already applied locally by the screen.</summary>
    public Task RecordCategoryAsync(string code, bool liked, CancellationToken cancellationToken = default) =>
        outbox.EnqueueAsync(new InteractionDto(Guid.NewGuid(), InteractionKinds.OnboardingCategory, null, clock.GetUtcNow(), CategoryCode: code, Value: liked ? 1d : -1d), cancellationToken);

    private async Task LearnLocallyAsync(InteractionDto dto, IReadOnlyDictionary<string, double> weights, CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        var interaction = new Interaction(dto.ClientEventId, dto.Kind, dto.PoiId?.ToString("D"), dto.OccurredAt, dto.Confidence ?? dto.Value ?? 0d, dto.CategoryCode);
        var options = new LearningOptions();
        var vector = new Dictionary<string, double>(profile.Affinities);

        if (dto.Kind == InteractionKinds.DislikeCategory && dto.CategoryCode is { } category)
        {
            vector = InterestLearning.Step(vector, InterestLearning.CategoryVector(category), options.DislikeCategoryIntensity, options.Eta, NoLocks, dto.OccurredAt);
        }
        else if (InterestLearning.Intensity(interaction, options) is { } intensity)
        {
            vector = InterestLearning.Step(vector, weights, intensity, options.Eta, NoLocks, dto.OccurredAt);
        }

        var excluded = new HashSet<Guid>(profile.Excluded);
        if (dto.Kind == InteractionKinds.DislikePoi && dto.PoiId is { } rejected)
        {
            excluded.Add(rejected);
        }

        await profiles.SaveAsync(profile with { Affinities = vector, Excluded = excluded, Depth = profile.Depth + InterestLearning.DepthPoints(interaction) }, cancellationToken);
    }

    private static readonly Dictionary<string, DateTimeOffset> NoLocks = [];
}

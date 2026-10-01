using FluentValidation;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Application.Features;

public sealed record IngestInteractionsCommand(Guid TravelerId, IReadOnlyList<InteractionDto> Interactions);

public sealed class IngestInteractionsValidator : AbstractValidator<IngestInteractionsCommand>
{
    public const int MaxBatch = 200;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    public IngestInteractionsValidator(TimeProvider clock)
    {
        RuleFor(c => c.Interactions).NotNull().Must(items => items.Count is > 0 and <= MaxBatch).WithMessage($"a batch holds 1 to {MaxBatch} interactions");
        RuleForEach(c => c.Interactions).ChildRules(item =>
        {
            item.RuleFor(i => i.ClientEventId).NotEqual(Guid.Empty);
            item.RuleFor(i => i.Kind).Must(InteractionKinds.All.Contains).WithMessage("unknown interaction kind");
            item.RuleFor(i => i.OccurredAt).LessThanOrEqualTo(_ => clock.GetUtcNow() + ClockSkew).WithMessage("occurredAt is in the future");
            item.RuleFor(i => i.PoiId).NotNull().When(i => i.Kind is not (InteractionKinds.OnboardingCategory or InteractionKinds.DislikeCategory)).WithMessage("poiId is required for this kind");
            item.RuleFor(i => i.CategoryCode).NotNull().Must(code => code is not null && Interests.LevelOne.Contains(code)).When(i => i.Kind is InteractionKinds.OnboardingCategory or InteractionKinds.DislikeCategory).WithMessage("categoryCode must be a level-1 code");
            item.RuleFor(i => i.Confidence).NotNull().InclusiveBetween(0d, 1d).When(i => i.Kind == InteractionKinds.Visit).WithMessage("a visit needs a confidence in [0, 1]");
            item.RuleFor(i => i.DwellS).NotNull().GreaterThanOrEqualTo(0).When(i => i.Kind == InteractionKinds.Visit).WithMessage("a visit needs dwellS");
            item.RuleFor(i => i.Surface).MaximumLength(32);
        });
    }
}

public static class IngestInteractionsHandler
{
    /// <summary>
    /// The history is the truth and the vector is its replay (§6.4): every batch stores the new events, plays the whole history in event order
    /// and saves the result. A late event (offline device, second device) therefore needs no special case, and a resend changes nothing.
    /// </summary>
    public static async Task<Result<InteractionBatchResponse>> Handle(
        IngestInteractionsCommand command,
        IDiscoveryStore store,
        IValidator<IngestInteractionsCommand> validator,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<InteractionBatchResponse>("validation", validation.Errors[0].ErrorMessage);
        }

        var incoming = Map(command.Interactions);
        var response = await Apply(store, command.TravelerId, incoming, cancellationToken);
        return Result.Success(response with { Duplicates = command.Interactions.Count - response.Accepted });
    }

    internal static IReadOnlyList<IncomingInteraction> Map(IEnumerable<InteractionDto> items) =>
        [.. items
            .DistinctBy(item => item.ClientEventId)
            .Select(item => new IncomingInteraction(
                new Interaction(
                    item.ClientEventId,
                    item.Kind,
                    item.PoiId?.ToString("D"),
                    item.OccurredAt.ToUniversalTime(),
                    item.Kind == InteractionKinds.Visit ? item.Confidence ?? 0d : item.Value ?? 0d,
                    item.CategoryCode),
                item.StoryId,
                item.StoryVersion,
                item.DwellS,
                item.Surface))];

    internal static Task<InteractionBatchResponse> Apply(IDiscoveryStore store, Guid travelerId, IReadOnlyList<IncomingInteraction> incoming, CancellationToken cancellationToken) =>
        store.ExclusiveAsync(travelerId, async session =>
        {
            var added = await session.AddAsync(incoming, cancellationToken);
            var learned = await Replay(session, cancellationToken);
            return new InteractionBatchResponse(
                learned.Vector,
                learned.ProfileDepth,
                Interests.Version,
                added,
                incoming.Count - added,
                [.. learned.Excluded.Select(Guid.Parse)]);
        }, cancellationToken);

    /// <summary>Replays the stored history with the traveler's locks and saves the result.</summary>
    internal static async Task<LearnedProfile> Replay(ITravelerSession session, CancellationToken cancellationToken)
    {
        var history = await session.HistoryAsync(cancellationToken);
        var weights = await session.PlaceWeightsAsync(history.Where(i => i.PoiId is not null).Select(i => i.PoiId!).Distinct(), cancellationToken);
        var locks = await session.LocksAsync(cancellationToken);
        var learned = InterestLearning.Replay(history, id => weights.GetValueOrDefault(id), locks.Until, pinned: locks.Pinned);
        await session.SaveAsync(learned, locks, cancellationToken);
        return learned;
    }
}

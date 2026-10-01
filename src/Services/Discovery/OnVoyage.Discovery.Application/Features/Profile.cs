using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Application.Features;

public sealed record GetProfileQuery(Guid TravelerId);

public static class GetProfileHandler
{
    /// <summary>Thresholds of §6.13.</summary>
    public static string Level(int depth) => depth switch { >= 100 => "very_rich", >= 50 => "rich", >= 10 => "interesting", _ => "new" };

    public static async Task<Result<ProfileDto>> Handle(GetProfileQuery query, IDiscoveryStore store, CancellationToken cancellationToken)
    {
        var stored = await store.GetProfileAsync(query.TravelerId, cancellationToken);
        if (stored is null)
        {
            return Result.Success(new ProfileDto(new Dictionary<string, double>(), 0, Level(0), Interests.Version, new Dictionary<string, DateTimeOffset>(), [], "personalized"));
        }

        return Result.Success(new ProfileDto(
            stored.Learned.Vector,
            stored.Learned.ProfileDepth,
            Level(stored.Learned.ProfileDepth),
            stored.TaxonomyVersion,
            stored.Locks.Until,
            [.. stored.Learned.Excluded.Select(Guid.Parse)],
            stored.Cohort));
    }
}

public sealed record CorrectProfileCommand(Guid TravelerId, IReadOnlyList<ProfileCorrection> Corrections);

public static class CorrectProfileHandler
{
    /// <summary>A hand-set dimension keeps its value and is not changed by learning for <c>LockDuration</c> (30 days).</summary>
    public static async Task<Result<ProfileDto>> Handle(CorrectProfileCommand command, IDiscoveryStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (command.Corrections.Count is 0 or > 100)
        {
            return Result.Failure<ProfileDto>("validation", "send between 1 and 100 corrections");
        }

        foreach (var correction in command.Corrections)
        {
            if (!Interests.All.Contains(correction.Code))
            {
                return Result.Failure<ProfileDto>("validation", $"unknown dimension '{correction.Code}'");
            }

            if (correction.Value is { } value && (value is < -1d or > 1d || double.IsNaN(value)))
            {
                return Result.Failure<ProfileDto>("validation", "values are between -1 and 1");
            }
        }

        var now = clock.GetUtcNow();
        var duration = new LearningOptions().LockDuration;
        await store.ExclusiveAsync(command.TravelerId, async session =>
        {
            var current = await session.LocksAsync(cancellationToken);
            var until = new Dictionary<string, DateTimeOffset>(current.Until);
            var pinned = new Dictionary<string, double>(current.Pinned);
            foreach (var correction in command.Corrections)
            {
                if (correction.Value is { } value)
                {
                    until[correction.Code] = now + duration;
                    pinned[correction.Code] = value;
                }
                else
                {
                    until.Remove(correction.Code); // unlocking keeps the value; learning moves it again from there
                }
            }

            // Replay saves with the new locks; the traveler's own history stays untouched.
            var history = await session.HistoryAsync(cancellationToken);
            var weights = await session.PlaceWeightsAsync(history.Where(i => i.PoiId is not null).Select(i => i.PoiId!).Distinct(), cancellationToken);
            var locks = new Locks(until, pinned);
            var learned = InterestLearning.Replay(history, id => weights.GetValueOrDefault(id), until, pinned: pinned);
            await session.SaveAsync(learned, locks, cancellationToken);
            return true;
        }, cancellationToken);

        return await GetProfileHandler.Handle(new GetProfileQuery(command.TravelerId), store, cancellationToken);
    }
}

public sealed record GetSettingsQuery(Guid TravelerId);

public sealed record UpdateSettingsCommand(Guid TravelerId, SettingsPatch Patch);

public static class GetSettingsHandler
{
    public static async Task<Result<SettingsDto>> Handle(GetSettingsQuery query, ITravelerReader travelers, CancellationToken cancellationToken)
    {
        var traveler = await travelers.GetAsync(query.TravelerId, cancellationToken);
        return Result.Success(new SettingsDto(traveler?.Lang ?? "fr", traveler?.EthicalMode ?? "balanced"));
    }
}

public static class UpdateSettingsHandler
{
    private static readonly string[] Modes = ["off", "balanced", "strong"];
    private static readonly string[] Languages = ["fr", "en"];

    public static async Task<Result<SettingsDto>> Handle(UpdateSettingsCommand command, IDiscoveryStore store, CancellationToken cancellationToken)
    {
        if (command.Patch.EthicalMode is { } mode && !Modes.Contains(mode))
        {
            return Result.Failure<SettingsDto>("validation", "ethicalMode is off, balanced or strong");
        }

        if (command.Patch.Lang is { } lang && !Languages.Contains(lang))
        {
            return Result.Failure<SettingsDto>("validation", "lang is fr or en");
        }

        var (l, m) = await store.UpdateSettingsAsync(command.TravelerId, command.Patch.Lang, command.Patch.EthicalMode, cancellationToken);
        return Result.Success(new SettingsDto(l, m));
    }
}

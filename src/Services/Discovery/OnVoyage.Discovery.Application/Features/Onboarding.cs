using System.Security.Cryptography;
using System.Text;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Discovery.Domain;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Application.Features;

public sealed record GetOnboardingClipsQuery(string Lang);

public static class OnboardingClips
{
    public static OnboardingClipDto ToDto(StoredClip clip, IMediaUrls media) => new(
        clip.StoryId,
        clip.PoiId,
        clip.Title,
        clip.Lang,
        media.Url(clip.AudioPath),
        clip.DurationSeconds,
        InterestLearning.DominantCategory(clip.Candidate.Weights) ?? string.Empty,
        [.. clip.Candidate.Weights.Where(pair => pair.Value > 0d).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key)]);

    /// <summary>The active selection when an editor made one, otherwise the automatic one (five distinct level-1 categories).</summary>
    public static IReadOnlyList<StoredClip> Choose(IReadOnlyList<StoredClip> clips)
    {
        var active = clips.Where(clip => clip.Active).ToArray();
        if (active.Length > 0)
        {
            return active;
        }

        var picked = OnboardingSelector.Select(clips.Select(clip => clip.Candidate)).Select(candidate => candidate.StoryId).ToHashSet();
        return [.. clips.Where(clip => picked.Contains(clip.StoryId)).OrderBy(clip => clip.StoryId)];
    }
}

public static class GetOnboardingClipsHandler
{
    public static async Task<Result<IReadOnlyList<OnboardingClipDto>>> Handle(GetOnboardingClipsQuery query, IOnboardingStore store, IMediaUrls media, CancellationToken cancellationToken)
    {
        var clips = await store.ListAsync(string.IsNullOrWhiteSpace(query.Lang) ? "fr" : query.Lang, cancellationToken);
        return Result.Success<IReadOnlyList<OnboardingClipDto>>([.. OnboardingClips.Choose(clips).Select(clip => OnboardingClips.ToDto(clip, media))]);
    }
}

public sealed record SubmitOnboardingCommand(Guid TravelerId, OnboardingRequest Request);

public static class SubmitOnboardingHandler
{
    /// <summary>
    /// §6.3: each rated clip is a 👍/👎 on the clip's place, each chosen or set-aside category a direct value. The events get ids derived from
    /// the traveler and the answer, so sending the screen twice stores it once. Skipping the onboarding (empty request) leaves the vector at zero.
    /// </summary>
    public static async Task<Result<InteractionBatchResponse>> Handle(
        SubmitOnboardingCommand command, IDiscoveryStore store, IOnboardingStore clips, TimeProvider clock, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var categories = request.LikedCategories.Concat(request.DislikedCategories).ToArray();
        if (categories.Any(code => !Interests.LevelOne.Contains(code)) || request.LikedCategories.Intersect(request.DislikedCategories).Any())
        {
            return Result.Failure<InteractionBatchResponse>("validation", "categories must be distinct level-1 codes");
        }

        if (request.Clips.Count > 20 || request.Clips.DistinctBy(answer => answer.StoryId).Count() != request.Clips.Count)
        {
            return Result.Failure<InteractionBatchResponse>("validation", "each clip is rated once");
        }

        var known = (await clips.ListAsync(null, cancellationToken)).ToDictionary(clip => clip.StoryId);
        if (request.Clips.Any(answer => !known.ContainsKey(answer.StoryId)))
        {
            return Result.Failure<InteractionBatchResponse>("validation", "unknown onboarding clip");
        }

        var now = clock.GetUtcNow();
        var step = 0;
        var incoming = new List<IncomingInteraction>();
        foreach (var answer in request.Clips)
        {
            var clip = known[answer.StoryId];
            incoming.Add(new IncomingInteraction(
                new Interaction(DeterministicId(command.TravelerId, "clip", answer.StoryId.ToString("N")), answer.Liked ? InteractionKinds.OnboardingUp : InteractionKinds.OnboardingDown, clip.PoiId.ToString("D"), now.AddMilliseconds(step++)),
                clip.StoryId, null, null, null));
        }

        foreach (var (code, liked) in request.LikedCategories.Select(c => (c, true)).Concat(request.DislikedCategories.Select(c => (c, false))))
        {
            incoming.Add(new IncomingInteraction(
                new Interaction(DeterministicId(command.TravelerId, "category", code), InteractionKinds.OnboardingCategory, null, now.AddMilliseconds(step++), liked ? 1d : -1d, code),
                null, null, null, null));
        }

        return Result.Success(await IngestInteractionsHandler.Apply(store, command.TravelerId, incoming, cancellationToken));
    }

    internal static Guid DeterministicId(Guid traveler, string scope, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{traveler:N}|{scope}|{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed record GetAdminClipsQuery(string Lang);

public static class GetAdminClipsHandler
{
    public static async Task<Result<AdminClipsDto>> Handle(GetAdminClipsQuery query, IOnboardingStore store, IMediaUrls media, CancellationToken cancellationToken)
    {
        var clips = await store.ListAsync(query.Lang, cancellationToken);
        return Result.Success(new AdminClipsDto(
            [.. OnboardingClips.Choose(clips).Select(clip => OnboardingClips.ToDto(clip, media))],
            [.. clips.Select(clip => OnboardingClips.ToDto(clip, media))]));
    }
}

public sealed record SetActiveClipsCommand(IReadOnlyList<Guid> StoryIds);

public static class SetActiveClipsHandler
{
    /// <summary>The editor's choice must be five clips whose places belong to five different level-1 categories (T-506).</summary>
    public static async Task<Result<bool>> Handle(SetActiveClipsCommand command, IOnboardingStore store, CancellationToken cancellationToken)
    {
        if (command.StoryIds.Distinct().Count() != OnboardingSelector.ClipCount || command.StoryIds.Count != OnboardingSelector.ClipCount)
        {
            return Result.Failure<bool>("validation", $"choose exactly {OnboardingSelector.ClipCount} different clips");
        }

        var all = await store.ListAsync(null, cancellationToken);
        var chosen = command.StoryIds.Select(id => all.FirstOrDefault(clip => clip.StoryId == id)).ToArray();
        if (chosen.Any(clip => clip is null))
        {
            return Result.Failure<bool>("validation", "unknown onboarding clip");
        }

        if (!OnboardingSelector.AreDistinct(chosen.Select(clip => clip!.Candidate)))
        {
            return Result.Failure<bool>("validation", "the five clips must come from five different level-1 categories");
        }

        return await store.SetActiveAsync(command.StoryIds, cancellationToken)
            ? Result.Success(true)
            : Result.Failure<bool>("validation", "unknown onboarding clip");
    }
}

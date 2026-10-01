using System.Text.Json;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Taxonomy;

namespace OnVoyage.App.Core.Onboarding;

public enum ClipVerdict
{
    Liked,
    Disliked,
}

/// <summary>
/// Onboarding by audio clips (F-02): five clips of five different categories to rate, an optional screen of level-1 categories, then the
/// destination. The answers become the initial vector on the server (§6.3); offline, they wait on the device and are sent again later, the
/// server counting each answer once. Skipping leaves the vector at zero (cold start, §6.7).
/// </summary>
public sealed class OnboardingService(IDiscoveryClient client, IProfileStore profiles, InteractionSender sender)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The clips from Discovery, or the last ones received when the network is out. Empty when neither exists.</summary>
    public async Task<IReadOnlyList<OnboardingClipDto>> LoadClipsAsync(string lang, CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        try
        {
            var clips = await client.GetOnboardingClipsAsync(lang, cancellationToken);
            if (clips.Count > 0)
            {
                await profiles.SaveAsync(profile with { OnboardingClipsJson = JsonSerializer.Serialize(clips, Json) }, cancellationToken);
            }

            return clips;
        }
        catch (HttpRequestException)
        {
            return profile.OnboardingClipsJson is { } cached ? JsonSerializer.Deserialize<List<OnboardingClipDto>>(cached, Json) ?? [] : [];
        }
    }

    /// <summary>Sends the answers; returns false when they could only be kept for later.</summary>
    public async Task<bool> SubmitAsync(
        IReadOnlyList<(Guid StoryId, ClipVerdict Verdict)> clips,
        IReadOnlyCollection<string> likedCategories,
        IReadOnlyCollection<string> dislikedCategories,
        CancellationToken cancellationToken)
    {
        var request = new OnboardingRequest(
            [.. clips.Select(c => new ClipAnswer(c.StoryId, c.Verdict == ClipVerdict.Liked))],
            [.. likedCategories.Where(Interests.LevelOne.Contains)],
            [.. dislikedCategories.Where(Interests.LevelOne.Contains)]);

        var profile = await profiles.LoadAsync(cancellationToken);
        var local = ProfileUpdater.ApplyOnboarding(profile, request.LikedCategories, request.DislikedCategories);
        try
        {
            var response = await client.PostOnboardingAsync(request, cancellationToken);
            await profiles.SaveAsync(local with { PendingOnboardingJson = null }, cancellationToken);
            await sender.ApplyAsync(response, cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            // The category choices already move the local profile; the clips count once the server has them.
            await profiles.SaveAsync(local with { PendingOnboardingJson = JsonSerializer.Serialize(request, Json) }, cancellationToken);
            return false;
        }
    }

    /// <summary>"Passer": no answers, a zero vector and the cold-start recommendations.</summary>
    public async Task SkipAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        await profiles.SaveAsync(profile with { OnboardingDone = true }, cancellationToken);
    }

    /// <summary>Sends again what could not be sent. Safe to call at every start: nothing pending, nothing happens.</summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        if (profile.PendingOnboardingJson is not { } json || JsonSerializer.Deserialize<OnboardingRequest>(json, Json) is not { } request)
        {
            return;
        }

        try
        {
            var response = await client.PostOnboardingAsync(request, cancellationToken);
            await profiles.SaveAsync((await profiles.LoadAsync(cancellationToken)) with { PendingOnboardingJson = null }, cancellationToken);
            await sender.ApplyAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Still offline: the next start tries again.
        }
    }
}

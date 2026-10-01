using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Interactions;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.App.Core.Feedback;

public enum FeedbackChoice
{
    Liked,
    Meh,
    NotForMe,
}

/// <summary>What "Pas pour moi" applies to (F-07): the place (excluded, small effect on its category) or its kind of place.</summary>
public enum NotForMeScope
{
    Place,
    Category,
}

public sealed record ListenedStory(Guid StoryId, Guid PoiId, string Title, IReadOnlyDictionary<string, double> Weights, PlayOrigin Origin, DateTimeOffset EndedAt)
{
    /// <summary>Dominant level-1 category of the place: what "ce type de lieu" means.</summary>
    public string? Category => InterestLearning.DominantCategory(Weights);
}

/// <summary>Tells the traveler something is worth hearing about; the phone apps show a system notification, the PWA an in-page card.</summary>
public interface ILocalNotifier
{
    Task NotifyAsync(string title, string body, CancellationToken cancellationToken);
}

public sealed class NullLocalNotifier : ILocalNotifier
{
    public Task NotifyAsync(string title, string body, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Feedback after listening (F-07). A story told on request ends with the banner "Cette histoire vous a plu ?"; stories told by the discovery
/// mode pile up unrated and, from two, the trip recap offers to rate them in one tap each. Listening signals (80 %, replay, early drop)
/// go to Discovery too: they are needed for the service itself and do not depend on the statistics consent.
/// </summary>
public sealed class FeedbackTracker : IDisposable
{
    public const int RecapThreshold = 2;

    private readonly AudioPlaybackController _audio;
    private readonly InteractionRecorder _recorder;
    private readonly ILocalNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly List<ListenedStory> _unrated = [];
    private ListenedStory? _banner;
    private bool _recapNotified;

    public FeedbackTracker(AudioPlaybackController audio, InteractionRecorder recorder, ILocalNotifier notifier, TimeProvider clock)
    {
        _audio = audio;
        _recorder = recorder;
        _notifier = notifier;
        _clock = clock;
        audio.StoryEnded += OnStoryEnded;
        audio.Listening += OnListening;
    }

    public event Action? Changed;

    /// <summary>The story just heard on request, waiting for a reaction.</summary>
    public ListenedStory? Banner => _banner;

    /// <summary>Stories of the discovery mode nobody reacted to yet.</summary>
    public IReadOnlyList<ListenedStory> Unrated => _unrated;

    public bool RecapAvailable => _unrated.Count >= RecapThreshold;

    public void Dispose()
    {
        _audio.StoryEnded -= OnStoryEnded;
        _audio.Listening -= OnListening;
    }

    public async Task RateAsync(Guid storyId, FeedbackChoice choice, NotForMeScope scope = NotForMeScope.Place, CancellationToken cancellationToken = default)
    {
        var story = _banner?.StoryId == storyId ? _banner : _unrated.FirstOrDefault(s => s.StoryId == storyId);
        if (story is null)
        {
            return;
        }

        Forget(storyId);
        var category = story.Category;
        var (kind, categoryCode) = (choice, scope) switch
        {
            (FeedbackChoice.Liked, _) => (InteractionKinds.Like, (string?)null),
            (FeedbackChoice.Meh, _) => (InteractionKinds.Meh, null),
            (FeedbackChoice.NotForMe, NotForMeScope.Category) when category is not null => (InteractionKinds.DislikeCategory, category),
            _ => (InteractionKinds.DislikePoi, null),
        };
        await _recorder.RecordAsync(kind, story.PoiId, story.Weights, story.StoryId, categoryCode, cancellationToken: cancellationToken);
    }

    /// <summary>Closes the banner without a reaction ("D'autres lieux comme celui-ci" does that too).</summary>
    public void DismissBanner()
    {
        _banner = null;
        Changed?.Invoke();
    }

    /// <summary>The recap is closed without rating: the stories stay unrated and are not offered again.</summary>
    public void DismissRecap()
    {
        _unrated.Clear();
        _recapNotified = false;
        Changed?.Invoke();
    }

    private void Forget(Guid storyId)
    {
        if (_banner?.StoryId == storyId)
        {
            _banner = null;
        }

        _unrated.RemoveAll(s => s.StoryId == storyId);
        if (_unrated.Count < RecapThreshold)
        {
            _recapNotified = false;
        }

        Changed?.Invoke();
    }

    private void OnStoryEnded(PlayRequest request, bool completed)
    {
        // Only a story heard to the end asks for a reaction; a skipped one already said what it had to (abandon signal).
        if (!completed)
        {
            return;
        }

        var story = new ListenedStory(request.StoryId, request.PoiId, request.Title, request.Weights ?? new Dictionary<string, double>(), request.Origin, _clock.GetUtcNow());
        if (request.Origin == PlayOrigin.Manual)
        {
            _banner = story;
        }
        else if (_unrated.All(s => s.StoryId != story.StoryId))
        {
            _unrated.Add(story);
        }

        Changed?.Invoke();
        if (RecapAvailable && !_recapNotified)
        {
            _recapNotified = true;
            _ = _notifier.NotifyAsync("Votre trajet en histoires", $"{_unrated.Count} histoires écoutées : qu'en avez-vous pensé ?", CancellationToken.None);
        }
    }

    private void OnListening(PlayRequest request, ListeningSignal signal)
    {
        var kind = signal switch
        {
            ListeningSignal.Listened80 => InteractionKinds.Listen80,
            ListeningSignal.Replay => InteractionKinds.Replay,
            _ => InteractionKinds.AbandonEarly,
        };
        _ = _recorder.RecordAsync(kind, request.PoiId, request.Weights ?? new Dictionary<string, double>(), request.StoryId);
    }
}

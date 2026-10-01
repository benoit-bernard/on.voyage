namespace OnVoyage.Factory.Domain.Content;

/// <summary>Lifecycle of a story (§8.2). Text is approved by a person before any audio is generated.</summary>
public enum ContentStatus
{
    Draft,
    AiGenerated,
    Checked,
    NeedsReview,
    Approved,
    AudioReady,
    Published,
    Suspended,
    Archived,
    Rejected,
    Failed,
}

public static class ContentTransitions
{
    private static readonly Dictionary<ContentStatus, ContentStatus[]> Allowed = new()
    {
        [ContentStatus.Draft] = [ContentStatus.AiGenerated, ContentStatus.Failed],
        [ContentStatus.AiGenerated] = [ContentStatus.Checked, ContentStatus.NeedsReview, ContentStatus.Failed],
        [ContentStatus.Checked] = [ContentStatus.Approved, ContentStatus.Rejected, ContentStatus.NeedsReview],
        [ContentStatus.NeedsReview] = [ContentStatus.Approved, ContentStatus.Rejected, ContentStatus.Checked],
        [ContentStatus.Approved] = [ContentStatus.AudioReady, ContentStatus.Failed, ContentStatus.NeedsReview],
        [ContentStatus.AudioReady] = [ContentStatus.Published, ContentStatus.Approved, ContentStatus.Failed],
        [ContentStatus.Published] = [ContentStatus.Suspended, ContentStatus.Archived],
        [ContentStatus.Suspended] = [ContentStatus.NeedsReview, ContentStatus.Published],
        [ContentStatus.Archived] = [],
        [ContentStatus.Rejected] = [],
        [ContentStatus.Failed] = [ContentStatus.Draft],
    };

    public static bool CanMove(ContentStatus from, ContentStatus to) => Allowed[from].Contains(to);

    /// <summary>Statuses from which a text edit is still allowed; a published story is edited by writing a new version.</summary>
    public static bool IsEditable(ContentStatus status) => status is ContentStatus.Checked or ContentStatus.NeedsReview or ContentStatus.AiGenerated;
}

/// <summary>Kinds of story and their targets (§8.5). Words are for French; a language with a different pace scales them.</summary>
public enum StoryKind
{
    Standard,
    Anecdote,
    OnboardingClip,
}

public sealed record LengthTarget(int MinWords, int MaxWords, int MinSeconds, int MaxSeconds)
{
    public static LengthTarget For(StoryKind kind) => kind switch
    {
        StoryKind.Standard => new LengthTarget(225, 375, 90, 150),
        StoryKind.Anecdote => new LengthTarget(40, 110, 15, 45),
        _ => new LengthTarget(35, 40, 15, 15),
    };
}

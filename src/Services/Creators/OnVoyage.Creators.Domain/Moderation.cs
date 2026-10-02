namespace OnVoyage.Creators.Domain;

public static class ModerationTargets
{
    public const string Creator = "creator";
    public const string Content = "content";
    public const string PlaceLink = "place_link";
    public const string Tip = "tip";

    public static IReadOnlyList<string> All { get; } = [Creator, Content, PlaceLink, Tip];
}

public static class ModerationStatuses
{
    public const string Open = "open";
    public const string Decided = "decided";
}

/// <summary>
/// A report and its decision (F-33). <c>ReporterRef</c> is the traveler who reported; it is dropped when that traveler's data is deleted.
/// A decision that restricts something must carry a statement of reasons (DSA art. 17).
/// </summary>
public sealed record ModerationCase(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string Reason,
    Guid? ReporterRef,
    string Status,
    string? Decision,
    string? StatementOfReasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt)
{
    public const string Dismissed = "dismissed";
    public const string Upheld = "upheld";
    public const int MaxStatement = 2000;

    public bool IsOpen => Status == ModerationStatuses.Open;

    public static ModerationCase Open(Guid id, string targetType, Guid targetId, string reason, Guid reporter, DateTimeOffset now) =>
        new(id, targetType, targetId, reason, reporter, ModerationStatuses.Open, null, null, now, null);

    public (ModerationCase? Case, Violation? Violation) Decide(string decision, string? statement, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return (null, new Violation("case_closed", "Ce signalement a déjà fait l'objet d'une décision."));
        }

        if (decision is not (Dismissed or Upheld))
        {
            return (null, new Violation("validation", "La décision est « dismissed » (classer) ou « upheld » (retenir)."));
        }

        var text = string.IsNullOrWhiteSpace(statement) ? null : statement.Trim();
        if (decision == Upheld && text is null)
        {
            return (null, new Violation("statement_required", "Une décision qui retire un contenu doit être motivée (exposé des motifs)."));
        }

        return text is { Length: > MaxStatement }
            ? (null, new Violation("validation", $"L'exposé des motifs fait {MaxStatement} caractères au plus."))
            : (this with { Status = ModerationStatuses.Decided, Decision = decision, StatementOfReasons = text, DecidedAt = now }, null);
    }
}

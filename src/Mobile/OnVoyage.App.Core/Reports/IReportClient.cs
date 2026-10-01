namespace OnVoyage.App.Core.Reports;

public enum ReportKind
{
    InaccurateFact,
    Pronunciation,
    ClosedOrMoved,
    Photo,
    Other,
}

/// <summary>Report an error on a story (F-20). Sent to Factory with the story and its text only: never a position.</summary>
public interface IReportClient
{
    public const int MaxLength = 500;

    Task ReportAsync(Guid storyId, ReportKind kind, string text, CancellationToken cancellationToken);
}

public static class ReportLabels
{
    public static string Label(ReportKind kind) => kind switch
    {
        ReportKind.InaccurateFact => "Fait inexact",
        ReportKind.Pronunciation => "Prononciation",
        ReportKind.ClosedOrMoved => "Lieu fermé ou déplacé",
        ReportKind.Photo => "Photo",
        _ => "Autre",
    };

    /// <summary>The reason as Factory stores it: the kind first, then the traveler's words, within the 500 characters of F-20.</summary>
    public static string Compose(ReportKind kind, string text)
    {
        var reason = $"[{kind}] {text.Trim()}";
        return reason.Length <= IReportClient.MaxLength ? reason : reason[..IReportClient.MaxLength];
    }
}

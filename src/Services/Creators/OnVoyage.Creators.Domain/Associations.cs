namespace OnVoyage.Creators.Domain;

public static class PlaceLinkStatuses
{
    public const string Proposed = "proposed";
    public const string Validated = "validated";
    public const string Rejected = "rejected";

    public static bool IsKnown(string? status) => status is Proposed or Validated or Rejected;
}

/// <summary>
/// A creator's association with a place (F-28): only <c>validated</c> links are ever published. <c>ContentId</c> is null for a tip alone.
/// </summary>
public sealed record PlaceLink(
    Guid Id,
    Guid CreatorId,
    Guid PoiId,
    Guid? ContentId,
    int? StartSeconds,
    double Confidence,
    string Status,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset CreatedAt,
    string Signals = "{}")
{
    public bool IsValidated => Status == PlaceLinkStatuses.Validated;

    public PlaceLink WithStatus(string status, DateTimeOffset now) =>
        this with { Status = status, ValidatedAt = status == PlaceLinkStatuses.Validated ? ValidatedAt ?? now : null };
}

public static class TipStatuses
{
    public const string Published = "published";
    public const string Hidden = "hidden";
}

/// <summary>"Viens au coucher du soleil, côté ouest": 280 characters at most, no rating (F-26).</summary>
public sealed record CreatorTip(Guid Id, Guid CreatorId, Guid PoiId, string Text, DateTimeOffset UpdatedAt, string Status)
{
    public const int MaxLength = 280;

    public static Violation? Check(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().Length > MaxLength
            ? new Violation("validation", $"Le conseil est obligatoire et fait {MaxLength} caractères au plus.")
            : null;
}

/// <summary>
/// The Creators copy of a place of the catalog (<c>creators.poi_directory</c>): names and city to match and search, never a coordinate.
/// </summary>
public sealed record PoiEntry(
    Guid PoiId,
    Guid DestinationId,
    string DestinationSlug,
    string NameFr,
    string? NameEn,
    IReadOnlyList<string> Aliases,
    string? City,
    int ImportanceScore,
    bool IsPublished,
    int Version)
{
    public string DisplayName => NameFr;

    /// <summary>Lowercase, accent-free text of every name, for the substring and trigram search.</summary>
    public string SearchText => string.Join(' ', new[] { NameFr, NameEn, City }.Concat(Aliases).Where(part => !string.IsNullOrWhiteSpace(part)).Select(Fold));

    public static string Fold(string? text)
    {
        var decomposed = (text ?? string.Empty).Trim().Normalize(System.Text.NormalizationForm.FormD);
        return new string([.. decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)]).ToLowerInvariant();
    }
}

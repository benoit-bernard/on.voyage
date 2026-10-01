namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class TravelerRow
{
    public Guid Id { get; set; }
    public string Lang { get; set; } = "fr";
    public string EthicalMode { get; set; } = "balanced";
    public bool IsPremium { get; set; }
    public int ProfileDepth { get; set; }
    public string Cohort { get; set; } = "personalized";
    public DateTimeOffset LastActiveAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class InterestVectorRow
{
    public Guid TravelerId { get; set; }

    /// <summary>One float per dimension in the order of <c>Interests.All</c> for <see cref="TaxonomyVersion"/>. (pgvector arrives with the neighbour search, T-504.)</summary>
    public float[] Vector { get; set; } = [];

    public int TaxonomyVersion { get; set; }
    public string Locks { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class InteractionRow
{
    public long Id { get; set; }
    public Guid TravelerId { get; set; }
    public Guid ClientEventId { get; set; }
    public Guid? PoiId { get; set; }
    public Guid? StoryId { get; set; }
    public int? StoryVersion { get; set; }
    public string Kind { get; set; } = "";
    public double Value { get; set; }
    public string? CategoryCode { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class PoiRatingRow
{
    public Guid TravelerId { get; set; }
    public Guid PoiId { get; set; }
    public double Rating { get; set; }
    public bool Excluded { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A probable visit. No coordinates, by design (D-14); a test keeps it that way.</summary>
internal sealed class VisitRow
{
    public long Id { get; set; }
    public Guid TravelerId { get; set; }
    public Guid ClientEventId { get; set; }
    public Guid PoiId { get; set; }
    public DateOnly VisitedOn { get; set; }
    public int DwellS { get; set; }
    public double Confidence { get; set; }
}

internal sealed class ImpressionRow
{
    public long Id { get; set; }
    public Guid TravelerId { get; set; }
    public Guid ClientEventId { get; set; }
    public Guid PoiId { get; set; }
    public string Surface { get; set; } = "";
    public DateTimeOffset ShownAt { get; set; }
}

internal sealed class PoiProjectionRow
{
    public Guid PoiId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int CrowdLevel { get; set; } = 1;
    public bool AccessRegulated { get; set; }
    public string Weights { get; set; } = "{}";
    public double Importance { get; set; }
    public double Quality { get; set; }
    public bool HiddenGem { get; set; }
    public bool Fragile { get; set; }
    public bool IsPublished { get; set; }
    public int Version { get; set; }
}

internal sealed class OnboardingClipRow
{
    public Guid StoryId { get; set; }
    public Guid PoiId { get; set; }
    public string Lang { get; set; } = "fr";
    public string Title { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public int DurationSeconds { get; set; }
    public bool Active { get; set; }
    public int Version { get; set; }
}

internal sealed class SavedPoiRow
{
    public Guid TravelerId { get; set; }
    public Guid PoiId { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

internal sealed class StoryProjectionRow
{
    public Guid StoryId { get; set; }
    public Guid PoiId { get; set; }
    public string Lang { get; set; } = "fr";
    public string Kind { get; set; } = "standard";
    public int DurationSeconds { get; set; }
    public bool IsPremium { get; set; }

    /// <summary>JSON object part name → media path (<c>main</c>, <c>announce_front</c>…).</summary>
    public string AudioParts { get; set; } = "{}";
    public int Version { get; set; }
}

internal sealed class CategoryAffinityRow
{
    public string CodeA { get; set; } = "";
    public string CodeB { get; set; } = "";
    public double Lift { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}

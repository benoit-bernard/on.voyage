namespace OnVoyage.Discovery.Contracts;

/// <summary>
/// One signal sent by the app (§12.4). <see cref="ClientEventId"/> makes a resend harmless. Visits carry the confidence and the stay,
/// never coordinates. <see cref="CategoryCode"/> is a level-1 code for <c>onboarding_category</c> and <c>dislike_category</c>.
/// </summary>
public sealed record InteractionDto(
    Guid ClientEventId,
    string Kind,
    Guid? PoiId,
    DateTimeOffset OccurredAt,
    Guid? StoryId = null,
    int? StoryVersion = null,
    string? CategoryCode = null,
    double? Value = null,
    int? DwellS = null,
    double? Confidence = null,
    string? Surface = null);

public sealed record InteractionBatchRequest(IReadOnlyList<InteractionDto> Interactions);

/// <summary>The server's vector after the batch: it replaces the app's copy. <see cref="Excluded"/> are the places the traveler turned down.</summary>
public sealed record InteractionBatchResponse(
    IReadOnlyDictionary<string, double> Vector,
    int ProfileDepth,
    int TaxonomyVersion,
    int Accepted,
    int Duplicates,
    IReadOnlyList<Guid> Excluded);

public sealed record ProfileDto(
    IReadOnlyDictionary<string, double> Vector,
    int ProfileDepth,
    string Level,
    int TaxonomyVersion,
    IReadOnlyDictionary<string, DateTimeOffset> Locks,
    IReadOnlyList<Guid> Excluded,
    string Cohort);

/// <summary>A manual correction (F-22): the dimension takes the value and is locked for 30 days. A null value only removes the lock.</summary>
public sealed record ProfileCorrection(string Code, double? Value);

public sealed record ProfileCorrectionRequest(IReadOnlyList<ProfileCorrection> Corrections);

public sealed record OnboardingClipDto(Guid StoryId, Guid PoiId, string Title, string Lang, string AudioUrl, int DurationSeconds, string Category, IReadOnlyList<string> TaxonomyCodes);

public sealed record ClipAnswer(Guid StoryId, bool Liked);

/// <summary>Answers of the three onboarding screens (§6.3). The same request sent twice gives the same profile.</summary>
public sealed record OnboardingRequest(IReadOnlyList<ClipAnswer> Clips, IReadOnlyList<string> LikedCategories, IReadOnlyList<string> DislikedCategories);

public sealed record ActiveClipsRequest(IReadOnlyList<Guid> StoryIds);

/// <summary>What the back-office shows: the clips served now and every clip that could be.</summary>
public sealed record AdminClipsDto(IReadOnlyList<OnboardingClipDto> Active, IReadOnlyList<OnboardingClipDto> Candidates);

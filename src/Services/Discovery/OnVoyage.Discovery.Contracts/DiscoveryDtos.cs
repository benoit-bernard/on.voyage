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

/// <summary>The explanation as a template code and its parameters (§6.9); the app owns the sentences, in French and English.</summary>
public sealed record WhyDto(string Template, IReadOnlyDictionary<string, string> Params);

public sealed record RecommendationItemDto(Guid PoiId, string Slug, string Name, double Score, int? Compatibility, WhyDto Why, bool IsExploration, Guid? IsAlternativeTo);

public sealed record RecommendationsDto(IReadOnlyList<RecommendationItemDto> Items, string Cohort, int WeightsVersion, DateTimeOffset GeneratedAt);

public sealed record AffinityDto(string Code, double Value);

public sealed record PlanPlaceDto(RecommendationItemDto Item, int LegMeters);

public sealed record PlanDayDto(int Day, IReadOnlyList<PlanPlaceDto> Places, int TotalMeters);

/// <summary>"[Destination] pour vous" (F-11): profile summary, nine places, and the day-by-day plan when <c>days</c> was given.</summary>
public sealed record DestinationForMeDto(
    string Slug,
    string Name,
    bool Remote,
    IReadOnlyList<AffinityDto> Strongest,
    IReadOnlyList<AffinityDto> Weakest,
    IReadOnlyList<RecommendationItemDto> Places,
    IReadOnlyList<PlanDayDto>? Plan,
    string Cohort,
    int WeightsVersion);

/// <summary><see cref="TextOnly"/>: published without a <c>main</c> audio part; the device reads the text of the story (fetched from the catalog) with its own voice.</summary>
public sealed record CandidateStoryDto(Guid StoryId, string Kind, int DurationSeconds, IReadOnlyDictionary<string, string> AudioParts, bool TextOnly = false);

/// <summary>A place the discovery mode may tell. <see cref="BaseScore"/> leaves out Distance, Context and CrowdPenalty: the device adds them (§12.4).</summary>
public sealed record CandidateDto(
    Guid PoiId,
    string Slug,
    string Name,
    double Latitude,
    double Longitude,
    double Importance,
    bool Fragile,
    bool CarAccessible,
    bool VisibleFromRoad,
    int CrowdLevel,
    double BaseScore,
    IReadOnlyList<CandidateStoryDto> Stories);

public sealed record CandidatesDto(IReadOnlyList<CandidateDto> Items, string Cohort, int WeightsVersion, DateTimeOffset GeneratedAt);

public sealed record SavedItemDto(Guid PoiId, string Slug, string Name, DateTimeOffset SavedAt);

public sealed record SavedGroupDto(string Destination, IReadOnlyList<SavedItemDto> Items);

public sealed record HistoryItemDto(Guid PoiId, string Slug, string Name, DateTimeOffset LastAt, bool Listened, bool Visited);

/// <summary>Language and ethical mode (<c>off</c>, <c>balanced</c>, <c>strong</c>). Consents belong to Platform.</summary>
public sealed record SettingsDto(string Lang, string EthicalMode);

public sealed record SettingsPatch(string? Lang, string? EthicalMode);

/// <summary>A creator for "Découvrir les créateurs" (§6.15). <see cref="Affinity"/> is <c>A(u, c)</c> in percent; the creator vector is not exposed.</summary>
public sealed record CreatorForMeDto(Guid CreatorId, string Handle, string DisplayName, string? AvatarPath, IReadOnlyList<string> Specialties, int PlaceCount, int Affinity, bool Following);

public sealed record CreatorsForMeDto(string Destination, IReadOnlyList<CreatorForMeDto> Items, string Cohort);

/// <summary>Precomputed scores for the offline pack: <see cref="Cf"/> is null until collaborative filtering exists; <see cref="CreatorSignal"/> is in [0, 1].</summary>
public sealed record CfScoreDto(Guid PoiId, double? Cf, double CreatorSignal);

public sealed record CfScoresDto(IReadOnlyList<CfScoreDto> Items, string Cohort, DateTimeOffset GeneratedAt);

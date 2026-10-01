using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Content;

public sealed record SourceDocument(
    Guid Id, Guid PlaceId, string Type, string Url, string Title, string? Publisher, string License, string Language,
    string? Revision, DateTimeOffset RetrievedAt, string Text, string Sha256, double Quality);

public sealed record FactRecord(
    Guid Id, Guid PlaceId, Guid DocumentId, string Statement, FactType Type, string Quote, double Confidence, FactStatus Status, string? Reason);

public enum SentenceVerdict
{
    /// <summary>One or more validated facts back the sentence.</summary>
    Supported,

    /// <summary>A transition with no factual content.</summary>
    Generic,

    /// <summary>States something no fact supports: the story goes to a person.</summary>
    Unsupported,
}

public sealed record VerifiedSentence(string Sentence, SentenceVerdict Verdict, IReadOnlyList<Guid> SupportingFacts);

public sealed record CheckReport(IReadOnlyList<CheckIssue> Issues, IReadOnlyList<VerifiedSentence> Sentences, OverlapResult? Overlap, int Attempts, IReadOnlyList<string> Uncertainties)
{
    public static CheckReport Empty { get; } = new([], [], null, 0, []);
}

public sealed record StoryRecord(
    Guid Id,
    Guid PlaceId,
    string Lang,
    StoryKind Kind,
    int Version,
    ContentStatus Status,
    string Title,
    string Hook,
    string Text,
    string RemoteIntro,
    string AnnounceFront,
    string AnnounceLeft,
    string AnnounceRight,
    string? CareNote,
    IReadOnlyList<Guid> FactsUsed,
    int EstimatedDurationSeconds,
    string PromptVersion,
    string Model,
    double QualityScore,
    CheckReport Report,
    string VoiceId,
    double EditorialScore,
    string? RejectedReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

public sealed record AudioPartRecord(string Part, string Path, string Sha256, int DurationSeconds, long Bytes);

public sealed record StoryReport(Guid Id, Guid StoryId, Guid TravelerId, string Reason, DateTimeOffset CreatedAt);

// ---- what the language-model ports exchange (the writer never receives source text: see StoryWriteRequest)

public sealed record CandidateFact(string Statement, string Type, string Quote, double Confidence);

public sealed record FactExtractionRequest(string PlaceName, string Language, string DocumentTitle, string DocumentText, Guid ContentId);

/// <summary>Facts only. There is deliberately no field for a source text: the writer cannot paraphrase what it was never given (D-08).</summary>
public sealed record StoryWriteRequest(
    string PlaceName,
    string DestinationName,
    string Language,
    StoryKind Kind,
    IReadOnlyList<string> Categories,
    IReadOnlyList<WriteFact> Facts,
    LengthTarget Target,
    Guid ContentId,
    string? Feedback,
    bool Fragile = false);

public sealed record WriteFact(Guid Id, string Statement);

public sealed record StoryDraft(
    string Title,
    string Hook,
    string Story,
    string RemoteIntro,
    string AnnounceFront,
    string AnnounceLeft,
    string AnnounceRight,
    string? CareNote,
    IReadOnlyList<Guid> FactsUsed,
    IReadOnlyList<string> Interests,
    IReadOnlyList<string> Uncertainties,
    int EstimatedDurationSeconds,
    string PromptVersion,
    string Model);

public sealed record StoryVerifyRequest(string Language, IReadOnlyList<string> Sentences, IReadOnlyList<WriteFact> Facts, Guid ContentId);

public sealed record SpeechRequest(string Text, string Voice, string Language, string Instructions);

public sealed record SpeechResult(byte[] Audio, string Provider, string Model, int InputCharacters);

public sealed record AudioTags(string Title, string ContentId, int ContentVersion, string TtsProvider, string TtsModel);

public sealed record ProcessedAudio(byte[] Mp3, int DurationSeconds, string Sha256);

public sealed record ContentSettings
{
    public int MinFacts { get; init; } = 3;
    public int WriterAttempts { get; init; } = 2;
    public int ReportSuspendThreshold { get; init; } = 3;
    public int ReportsPerTravelerPerDay { get; init; } = 10;
    public string DefaultVoiceFr { get; init; } = "marin";
    public string DefaultVoiceEn { get; init; } = "cedar";
    public string SpeechInstructions { get; init; } = "Conteur chaleureux, français de France, rythme posé, sourire dans la voix.";
    public IReadOnlyDictionary<string, double> SourceQuality { get; init; } = new Dictionary<string, double>
    {
        ["official"] = 1.0,
        ["merimee"] = 0.95,
        ["wikipedia"] = 0.7,
        ["wikidata"] = 0.7,
        ["other"] = 0.5,
    };

    public string VoiceFor(string language) => language == "en" ? DefaultVoiceEn : DefaultVoiceFr;
}

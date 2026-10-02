using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Content;

/// <summary>A Wikipedia article as plain text (<c>action=query&amp;prop=extracts&amp;explaintext=1</c>), with its revision.</summary>
public sealed record WikipediaText(string Language, string Title, string Url, string Text, string Revision);

public interface IWikipediaTextClient
{
    Task<WikipediaText?> GetExtractAsync(string language, string title, CancellationToken cancellationToken);
}

/// <summary>Structured-output extraction of §8.4. The caller validates every quote; implementations do not.</summary>
public interface IFactExtractor
{
    Task<IReadOnlyList<CandidateFact>> ExtractAsync(FactExtractionRequest request, CancellationToken cancellationToken);
}

public interface IStoryWriter
{
    Task<StoryDraft> WriteAsync(StoryWriteRequest request, CancellationToken cancellationToken);
}

/// <summary>Independent second call that judges every sentence against the facts (§8.6).</summary>
public interface IStoryVerifier
{
    Task<IReadOnlyList<VerifiedSentence>> VerifyAsync(StoryVerifyRequest request, CancellationToken cancellationToken);
}

public interface ITextToSpeechProvider
{
    /// <summary>False when no voice is configured: stories are then published as text only (<c>audio_status</c> pending) instead of failing.</summary>
    bool IsAvailable => true;

    Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}

/// <summary>ffmpeg loudness normalisation, mono 44.1 kHz MP3 at the configured bit rate, silence trimming, ID3 tags (§8.7, §8.8).</summary>
public interface IAudioProcessor
{
    Task<ProcessedAudio> ProcessAsync(byte[] input, AudioTags tags, CancellationToken cancellationToken);
}

public interface IMediaStorage
{
    /// <summary>Stores the file under the relative path and returns the path.</summary>
    Task<string> SaveAsync(string relativePath, byte[] content, CancellationToken cancellationToken);
}

/// <summary>An external service (language model, speech, Wikimedia) failed in a way worth retrying. Wolverine retries the job.</summary>
public sealed class ExternalServiceException(string message, Exception? inner = null) : Exception(message, inner);

public interface IContentStore
{
    // Sources and facts
    Task<IReadOnlyList<SourceDocument>> ListDocumentsAsync(Guid placeId, CancellationToken cancellationToken);

    Task<bool> SaveDocumentAsync(SourceDocument document, CancellationToken cancellationToken);

    Task<IReadOnlyList<FactRecord>> ListFactsAsync(Guid placeId, CancellationToken cancellationToken);

    Task<FactRecord?> FindFactAsync(Guid factId, CancellationToken cancellationToken);

    Task AddFactsAsync(IReadOnlyList<FactRecord> facts, CancellationToken cancellationToken);

    Task SetFactStatusAsync(IReadOnlyCollection<Guid> factIds, FactStatus status, string? reason, CancellationToken cancellationToken);

    // Stories
    Task<IReadOnlyList<StoryRecord>> ListStoriesAsync(Guid placeId, CancellationToken cancellationToken);

    Task<StoryRecord?> FindStoryAsync(Guid storyId, CancellationToken cancellationToken);

    Task<int> NextVersionAsync(Guid placeId, string lang, StoryKind kind, CancellationToken cancellationToken);

    Task SaveStoryAsync(StoryRecord story, CancellationToken cancellationToken);

    Task<IReadOnlyList<AudioPartRecord>> ListAudioPartsAsync(Guid storyId, CancellationToken cancellationToken);

    Task SaveAudioPartsAsync(Guid storyId, IReadOnlyList<AudioPartRecord> parts, CancellationToken cancellationToken);

    Task DeleteAudioPartsAsync(Guid storyId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetPronunciationAsync(string destinationSlug, CancellationToken cancellationToken);

    /// <summary>Persists the story change and the integration events in one transaction (outbox).</summary>
    Task SaveStoryWithEventsAsync(StoryRecord story, IReadOnlyList<object> integrationEvents, StoryRecord? archived, CancellationToken cancellationToken);

    // Reports (F-20)
    Task<int> CountReportsByTravelerSinceAsync(Guid travelerId, DateTimeOffset since, CancellationToken cancellationToken);

    Task<bool> AddReportAsync(StoryReport report, CancellationToken cancellationToken);

    /// <summary>Distinct readers with an open report on the story; <paramref name="inaccurateFactOnly"/> keeps the reports that count toward the suspension.</summary>
    Task<int> CountDistinctReportersAsync(Guid storyId, bool inaccurateFactOnly, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryReport>> ListReportsAsync(Guid storyId, CancellationToken cancellationToken);

    /// <summary>Stories that have reports, newest first. <paramref name="status"/> keeps the stories that have at least one report in that status, <paramref name="kind"/> the stories with at least one report of that kind.</summary>
    Task<IReadOnlyList<ReportInboxItem>> ListReportInboxAsync(string? status, string? kind, int limit, CancellationToken cancellationToken);

    /// <summary>Closes the story's open reports with a status and a note; returns how many were open.</summary>
    Task<int> ResolveReportsAsync(Guid storyId, string status, string? resolution, DateTimeOffset at, CancellationToken cancellationToken);
}

public interface IContentSettingsProvider
{
    ContentSettings Current { get; }
}

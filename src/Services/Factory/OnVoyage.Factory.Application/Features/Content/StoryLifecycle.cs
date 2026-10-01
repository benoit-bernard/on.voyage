using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Contracts;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Features.Content;

public sealed record ListStoriesQuery(Guid PlaceId);

public sealed record GetStoryQuery(Guid StoryId);

public sealed record EditStoryTextCommand(Guid StoryId, string Title, string Text);

public sealed record ApproveStoryCommand(Guid StoryId, double? EditorialScore);

public sealed record RejectStoryCommand(Guid StoryId, string Reason);

public sealed record SuspendStoryCommand(Guid StoryId, string Reason);

public sealed record ResumeStoryCommand(Guid StoryId);

public sealed record OpenCorrectionCommand(Guid StoryId);

public sealed record PublishStoryCommand(Guid StoryId);

public sealed record GenerateAudioCommand(Guid StoryId);

public sealed record ReportStoryCommand(Guid StoryId, Guid TravelerId, string Reason);

public sealed record ChangeStoryVoiceCommand(Guid StoryId, string Voice);

public sealed record ResetAudioCommand(Guid StoryId);

public sealed record StoryView(StoryRecord Story, IReadOnlyList<AudioPartRecord> Parts, IReadOnlyList<StoryReport> Reports, IReadOnlyList<FactRecord> Facts);

public static class StoryQueryHandler
{
    public static async Task<Result<IReadOnlyList<StoryRecord>>> Handle(ListStoriesQuery query, IContentStore content, CancellationToken cancellationToken) =>
        Result.Success(await content.ListStoriesAsync(query.PlaceId, cancellationToken));

    public static async Task<Result<StoryView>> Handle(GetStoryQuery query, IContentStore content, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(query.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryView>("story_not_found", "Story not found.");
        }

        var facts = (await content.ListFactsAsync(story.PlaceId, cancellationToken)).Where(fact => story.FactsUsed.Contains(fact.Id)).ToList();
        return Result.Success(new StoryView(story, await content.ListAudioPartsAsync(story.Id, cancellationToken), await content.ListReportsAsync(story.Id, cancellationToken), facts));
    }
}

public static class StoryEditorialHandler
{
    private static Result<T> Wrong<T>(StoryRecord story, ContentStatus to) =>
        Result.Failure<T>("invalid_transition", $"A story in state {story.Status} cannot move to {to}.");

    /// <summary>
    /// A person corrects the text. Only the automatic checks that do not need a model are re-run; the model's sentence verdicts of the
    /// original text are dropped because they no longer describe it.
    /// </summary>
    public static async Task<Result<StoryRecord>> Handle(EditStoryTextCommand command, IContentStore content, IPlaceStore places, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (!ContentTransitions.IsEditable(story.Status))
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"A story in state {story.Status} cannot be edited; open a correction instead.");
        }

        if (string.IsNullOrWhiteSpace(command.Text) || string.IsNullOrWhiteSpace(command.Title))
        {
            return Result.Failure<StoryRecord>("validation", "Title and text are required.");
        }

        var issues = new List<CheckIssue>();
        if (LengthCheck.Check(command.Text, story.Kind) is { } length)
        {
            issues.Add(length);
        }

        var style = StyleCheck.Check(command.Text);
        issues.AddRange(style);
        issues.AddRange(SafetyCheck.Check(command.Title + "\n" + command.Text));
        var documents = await content.ListDocumentsAsync(story.PlaceId, cancellationToken);
        var overlap = VerbatimOverlapDetector.Worst(command.Text, documents.Select(document => document.Text));
        if (!overlap.Passes)
        {
            issues.Add(new CheckIssue("overlap", $"Shares {overlap.LongestSharedWords} consecutive words or {overlap.FiveGramJaccard:0.000} of its 5-grams with a source."));
        }

        var status = issues.Count == 0 ? ContentStatus.Checked : ContentStatus.NeedsReview;
        var edited = story with
        {
            Title = command.Title.Trim(),
            Text = command.Text.Trim(),
            Status = status,
            Report = new CheckReport(issues, [], overlap, story.Report.Attempts, story.Report.Uncertainties),
            UpdatedAt = clock.GetUtcNow(),
        };
        await content.SaveStoryAsync(edited, cancellationToken);
        _ = places;
        return Result.Success(edited);
    }

    public static async Task<Result<StoryRecord>> Handle(ApproveStoryCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (!ContentTransitions.CanMove(story.Status, ContentStatus.Approved))
        {
            return Wrong<StoryRecord>(story, ContentStatus.Approved);
        }

        var editorial = Math.Clamp(command.EditorialScore ?? story.EditorialScore, 0d, 1d);

        // The editorial note counts for a tenth of the score (§8.9); the other components are unchanged, so scale around the old note.
        var quality = Math.Round(story.QualityScore + (0.10 * (editorial - story.EditorialScore)), 4);
        var approved = story with { Status = ContentStatus.Approved, EditorialScore = editorial, QualityScore = quality, UpdatedAt = clock.GetUtcNow() };
        await content.SaveStoryAsync(approved, cancellationToken);
        return Result.Success(approved);
    }

    public static async Task<Result<StoryRecord>> Handle(RejectStoryCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure<StoryRecord>("validation", "A reason is required to reject a story.");
        }

        if (!ContentTransitions.CanMove(story.Status, ContentStatus.Rejected))
        {
            return Wrong<StoryRecord>(story, ContentStatus.Rejected);
        }

        var rejected = story with { Status = ContentStatus.Rejected, RejectedReason = command.Reason.Trim(), UpdatedAt = clock.GetUtcNow() };
        await content.SaveStoryAsync(rejected, cancellationToken);
        return Result.Success(rejected);
    }

    /// <summary>"Open a correction": the suspended text is copied into a new version waiting for review; the suspended version stays off until a replacement is published.</summary>
    public static async Task<Result<StoryRecord>> Handle(OpenCorrectionCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (!ContentTransitions.CanMove(story.Status, ContentStatus.NeedsReview) || story.Status != ContentStatus.Suspended)
        {
            return Wrong<StoryRecord>(story, ContentStatus.NeedsReview);
        }

        var now = clock.GetUtcNow();
        var correction = story with
        {
            Id = Guid.CreateVersion7(),
            Version = await content.NextVersionAsync(story.PlaceId, story.Lang, story.Kind, cancellationToken),
            Status = ContentStatus.NeedsReview,
            RejectedReason = null,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = null,
        };
        await content.SaveStoryAsync(correction, cancellationToken);
        return Result.Success(correction);
    }
}

public static class StoryAudioAdminHandler
{
    private static readonly System.Text.RegularExpressions.Regex VoiceName = new("^[a-z][a-z0-9_-]{1,39}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A new voice applies to audio not yet made. If the audio exists (waiting to be listened to), it is thrown away and the story goes
    /// back to "approved": a voice is never swapped under a text a person already listened to.
    /// </summary>
    public static async Task<Result<StoryRecord>> Handle(ChangeStoryVoiceCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        var voice = command.Voice.Trim().ToLowerInvariant();
        if (!VoiceName.IsMatch(voice))
        {
            return Result.Failure<StoryRecord>("validation", "A voice name is lowercase letters, digits, - or _.");
        }

        if (story.Status is not (ContentStatus.Checked or ContentStatus.NeedsReview or ContentStatus.Approved or ContentStatus.AudioReady))
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"The voice of a story in state {story.Status} cannot change.");
        }

        var status = story.Status;
        if (status == ContentStatus.AudioReady)
        {
            await content.DeleteAudioPartsAsync(story.Id, cancellationToken);
            status = ContentStatus.Approved;
        }

        var changed = story with { VoiceId = voice, Status = status, UpdatedAt = clock.GetUtcNow() };
        await content.SaveStoryAsync(changed, cancellationToken);
        return Result.Success(changed);
    }

    /// <summary>"Regenerate the audio": drops the parts and goes back to approved so the voice job can run again.</summary>
    public static async Task<Result<StoryRecord>> Handle(ResetAudioCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (!ContentTransitions.CanMove(story.Status, ContentStatus.Approved) || story.Status != ContentStatus.AudioReady)
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"The audio of a story in state {story.Status} cannot be regenerated.");
        }

        await content.DeleteAudioPartsAsync(story.Id, cancellationToken);
        var reset = story with { Status = ContentStatus.Approved, UpdatedAt = clock.GetUtcNow() };
        await content.SaveStoryAsync(reset, cancellationToken);
        return Result.Success(reset);
    }
}

public static class StoryPublicationHandler
{
    /// <summary>Publishes a story whose text a person approved and whose audio was generated and listened to (§8.2). Never automatic.</summary>
    public static async Task<Result<StoryRecord>> Handle(
        PublishStoryCommand command, IContentStore content, IPlaceStore places, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (!ContentTransitions.CanMove(story.Status, ContentStatus.Published) || story.Status != ContentStatus.AudioReady)
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"A story in state {story.Status} cannot be published.");
        }

        var place = await places.FindAsync(story.PlaceId, cancellationToken);
        if (place is null || place.Status != PlaceStatus.Published)
        {
            // The catalog needs the place before its story; publish the place first.
            return Result.Failure<StoryRecord>("place_not_published", "Publish the place before its story.");
        }

        var parts = await content.ListAudioPartsAsync(story.Id, cancellationToken);
        if (parts.All(part => part.Part != "main"))
        {
            return Result.Failure<StoryRecord>("audio_missing", "The story has no audio.");
        }

        var now = clock.GetUtcNow();
        var previous = (await content.ListStoriesAsync(story.PlaceId, cancellationToken))
            .FirstOrDefault(other => other.Lang == story.Lang && other.Kind == story.Kind && other.Status == ContentStatus.Published && other.Id != story.Id);
        var archived = previous is null ? null : previous with { Status = ContentStatus.Archived, UpdatedAt = now };
        var published = story with { Status = ContentStatus.Published, PublishedAt = now, UpdatedAt = now };

        List<object> events = [];
        if (archived is not null)
        {
            events.Add(new StoryArchivedV1(Guid.CreateVersion7(), now, archived.Id, archived.PlaceId, archived.Version));
        }

        events.Add(await BuildPublishedEventAsync(published, parts, content, now, cancellationToken));
        await content.SaveStoryWithEventsAsync(published, events, archived, cancellationToken);
        return Result.Success(published);
    }

    public static async Task<Result<StoryRecord>> Handle(SuspendStoryCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure<StoryRecord>("validation", "A reason is required.");
        }

        return await SuspendAsync(story, command.Reason.Trim(), content, clock);
    }

    internal static async Task<Result<StoryRecord>> SuspendAsync(StoryRecord story, string reason, IContentStore content, TimeProvider clock)
    {
        if (!ContentTransitions.CanMove(story.Status, ContentStatus.Suspended))
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"A story in state {story.Status} cannot be suspended.");
        }

        var now = clock.GetUtcNow();
        var suspended = story with { Status = ContentStatus.Suspended, UpdatedAt = now };
        await content.SaveStoryWithEventsAsync(suspended, [new StoryUnpublishedV1(Guid.CreateVersion7(), now, story.Id, story.PlaceId, story.Version, reason)], null, CancellationToken.None);
        return Result.Success(suspended);
    }

    /// <summary>Back online without changing the text, after a review (<c>/resume</c> in the specification).</summary>
    public static async Task<Result<StoryRecord>> Handle(ResumeStoryCommand command, IContentStore content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<StoryRecord>("story_not_found", "Story not found.");
        }

        if (story.Status != ContentStatus.Suspended)
        {
            return Result.Failure<StoryRecord>("invalid_transition", $"A story in state {story.Status} cannot be resumed.");
        }

        var now = clock.GetUtcNow();
        var resumed = story with { Status = ContentStatus.Published, UpdatedAt = now };
        var parts = await content.ListAudioPartsAsync(story.Id, cancellationToken);
        await content.SaveStoryWithEventsAsync(resumed, [await BuildPublishedEventAsync(resumed, parts, content, now, cancellationToken)], null, cancellationToken);
        return Result.Success(resumed);
    }

    private static async Task<StoryPublishedV1> BuildPublishedEventAsync(StoryRecord story, IReadOnlyList<AudioPartRecord> parts, IContentStore content, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var facts = (await content.ListFactsAsync(story.PlaceId, cancellationToken)).Where(fact => story.FactsUsed.Contains(fact.Id)).ToList();
        var documents = (await content.ListDocumentsAsync(story.PlaceId, cancellationToken)).Where(document => facts.Any(fact => fact.DocumentId == document.Id));

        return new StoryPublishedV1(
            Guid.CreateVersion7(),
            now,
            story.Id,
            story.PlaceId,
            story.Lang,
            story.Kind switch { StoryKind.Standard => "standard", StoryKind.Anecdote => "anecdote", _ => "onboarding_clip" },
            story.Version,
            story.Title,
            story.Hook,
            story.Text,
            story.RemoteIntro,
            parts.Where(part => part.Part == "main").Select(part => part.DurationSeconds).FirstOrDefault(story.EstimatedDurationSeconds),
            story.VoiceId,
            true,
            false,
            [.. parts.Select(part => new StoryAudioPartV1(part.Part, part.Path, part.Sha256, part.DurationSeconds))],
            [.. documents.Select(document => new StorySourceV1(document.Title, document.Publisher, document.Url, document.License)).DistinctBy(source => source.Url)]);
    }
}

public static class StoryReportHandler
{
    /// <summary>
    /// A traveler flags an error (F-20). One report per traveler and story, a daily cap per traveler, and when enough different
    /// travelers agree the story is suspended until an editor looks at it.
    /// </summary>
    public static async Task<Result<bool>> Handle(ReportStoryCommand command, IContentStore content, IContentSettingsProvider settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return Result.Failure<bool>("validation", "Describe the problem in up to 500 characters.");
        }

        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null || story.Status != ContentStatus.Published)
        {
            return Result.Failure<bool>("story_not_found", "Story not found.");
        }

        var now = clock.GetUtcNow();
        if (await content.CountReportsByTravelerSinceAsync(command.TravelerId, now.AddDays(-1), cancellationToken) >= settings.Current.ReportsPerTravelerPerDay)
        {
            return Result.Failure<bool>("report_limit", "Too many reports today.");
        }

        // A repeated report from the same traveler is accepted silently so it cannot be used to count readers.
        await content.AddReportAsync(new StoryReport(Guid.CreateVersion7(), story.Id, command.TravelerId, reason, now), cancellationToken);

        if (await content.CountDistinctReportersAsync(story.Id, cancellationToken) >= settings.Current.ReportSuspendThreshold)
        {
            await StoryPublicationHandler.SuspendAsync(story, "reports_threshold", content, clock);
        }

        return Result.Success(true);
    }
}

public static class GenerateAudioHandler
{
    private static readonly string[] PartNames = ["main", "remote_intro", "announce_front", "announce_left", "announce_right"];

    /// <summary>
    /// Voices an approved story, once (§8.7): the text is never voiced before a person approved it, and a part already stored for this
    /// story and voice is not synthesised again.
    /// </summary>
    public static async Task<Result<IReadOnlyList<AudioPartRecord>>> Handle(
        GenerateAudioCommand command,
        Wolverine.Envelope? envelope,
        IContentStore content,
        IPlaceStore places,
        ITextToSpeechProvider speech,
        IAudioProcessor processor,
        IMediaStorage storage,
        IContentSettingsProvider settingsProvider,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var story = await content.FindStoryAsync(command.StoryId, cancellationToken);
        if (story is null)
        {
            return Result.Failure<IReadOnlyList<AudioPartRecord>>("story_not_found", "Story not found.");
        }

        if (story.Status != ContentStatus.Approved)
        {
            return Result.Failure<IReadOnlyList<AudioPartRecord>>("invalid_transition", $"Audio is only generated for approved stories (this one is {story.Status}).");
        }

        var place = await places.FindAsync(story.PlaceId, cancellationToken);
        var lexicon = await content.GetPronunciationAsync(place?.DestinationSlug ?? string.Empty, cancellationToken);
        var settings = settingsProvider.Current;
        var existing = (await content.ListAudioPartsAsync(story.Id, cancellationToken)).ToDictionary(part => part.Part);
        var kind = story.Kind switch { StoryKind.Standard => "standard", StoryKind.Anecdote => "anecdote", _ => "onboarding_clip" };

        try
        {
            var parts = new List<AudioPartRecord>();
            foreach (var name in PartNames)
            {
                if (existing.TryGetValue(name, out var done))
                {
                    parts.Add(done);
                    continue;
                }

                var text = name switch
                {
                    "main" => story.Text,
                    "remote_intro" => story.RemoteIntro,
                    "announce_front" => story.AnnounceFront,
                    "announce_left" => story.AnnounceLeft,
                    _ => story.AnnounceRight,
                };
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var spoken = PronunciationLexicon.Apply(text, lexicon);
                var raw = await speech.SynthesizeAsync(new SpeechRequest(spoken, story.VoiceId, story.Lang, settings.SpeechInstructions), cancellationToken);
                var processed = await processor.ProcessAsync(raw.Audio, new AudioTags(story.Title, story.Id.ToString(), story.Version, raw.Provider, raw.Model), cancellationToken);
                var path = $"audio/{story.PlaceId}/{story.Lang}/{kind}/v{story.Version}_{story.VoiceId}_{name}.mp3";
                await storage.SaveAsync(path, processed.Mp3, cancellationToken);
                var record = new AudioPartRecord(name, path, processed.Sha256, processed.DurationSeconds, processed.Mp3.LongLength);
                await content.SaveAudioPartsAsync(story.Id, [record], cancellationToken);
                parts.Add(record);
            }

            if (parts.All(part => part.Part != "main"))
            {
                return Result.Failure<IReadOnlyList<AudioPartRecord>>("validation", "The story has no text to voice.");
            }

            await content.SaveStoryAsync(story with { Status = ContentStatus.AudioReady, UpdatedAt = clock.GetUtcNow() }, cancellationToken);
            return Result.Success<IReadOnlyList<AudioPartRecord>>(parts);
        }
        catch (ExternalServiceException) when (envelope is { Attempts: >= WriteStoryHandler.MaxJobAttempts })
        {
            await content.SaveStoryAsync(story with { Status = ContentStatus.Failed, UpdatedAt = clock.GetUtcNow() }, cancellationToken);
            throw;
        }
    }
}

using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Dedup;
using OnVoyage.Factory.Domain.Scoring;

namespace OnVoyage.Factory.Application.Features.ScorePlaces;

public sealed record ScorePlacesCommand(string DestinationSlug);

public sealed record ScoreSummary(int Places, int AutoMerged, int Proposed, int NeedsReview, int HiddenGems);

public static class ScorePlacesHandler
{
    // Size of the spatial grid used to avoid comparing every pair: about 110 m, wider than the 75 m merge distance.
    private const double CellDegrees = 0.001;

    public static async Task<Result<ScoreSummary>> Handle(
        ScorePlacesCommand command,
        IPlaceStore places,
        IClassificationRuleProvider ruleProvider,
        IPlaceModelClassifier modelClassifier,
        HeritageClasses heritage,
        TimeProvider clock,
        ILogger<ScorePlacesCommand> logger,
        CancellationToken cancellationToken)
    {
        var autoMerged = await DeduplicateAsync(command.DestinationSlug, places, clock, cancellationToken);
        var active = (await places.ListActiveAsync(command.DestinationSlug, cancellationToken)).ToList();
        var proposals = (await places.ListDedupLinksAsync(command.DestinationSlug, true, cancellationToken)).Count;

        var ruleClassifier = new RuleClassifier(ruleProvider.Current);
        var classified = new Dictionary<Guid, (ClassificationOutcome Outcome, TaxonomyVector Vector, double Confidence)>();
        foreach (var place in active)
        {
            var byRules = ruleClassifier.Classify(new ClassificationInput(place.OsmTags, place.Enrichment?.InstanceOf ?? []));
            ModelClassification? byModel = null;
            if (!byRules.Vector.CoversALevelTwoCategory)
            {
                byModel = await modelClassifier.ClassifyAsync(
                    new PlaceDescription(place.Name, place.Enrichment?.DescriptionFr ?? place.Enrichment?.DescriptionEn, place.OsmTags, place.Enrichment?.InstanceOf ?? []), cancellationToken);
            }

            var (outcome, vector) = ClassificationDecision.Decide(byRules, byModel);
            classified[place.Id] = (outcome, vector, outcome == ClassificationOutcome.Rules ? 1d : byModel?.Confidence ?? 0d);
        }

        var percentiles = PopularityPercentiles.Compute(active.ToDictionary(place => place.Id, place => place.AnnualPageviews));
        var scorings = new List<PlaceScoring>(active.Count);
        foreach (var place in active)
        {
            var (outcome, vector, confidence) = classified[place.Id];
            var heritageValues = place.Enrichment?.HeritageStatuses ?? [];
            var importance = ImportanceScorer.Score(new ImportanceInput(
                heritage.IsUnesco(heritageValues),
                heritage.StatusOf(heritageValues),
                place.Enrichment?.Sitelinks ?? 0,
                place.AnnualPageviews,
                place.OsmTags.TryGetValue("tourism", out var tourism) && tourism == "attraction",
                place.ImportanceOverride));
            var percentile = percentiles[place.Id];
            var crowd = CrowdProfileBuilder.Build(percentile, vector, place.EditoriallySaturated);

            scorings.Add(new PlaceScoring(
                place.Id,
                importance,
                percentile,
                HiddenGemRule.IsHiddenGem(importance, percentile, crowd),
                crowd,
                vector,
                outcome,
                confidence,
                NextStatus(place.Status, outcome)));
        }

        await places.SaveScoringAsync(scorings, cancellationToken);

        var summary = new ScoreSummary(
            scorings.Count,
            autoMerged,
            proposals,
            scorings.Count(scoring => scoring.Status == PlaceStatus.NeedsReview),
            scorings.Count(scoring => scoring.HiddenGem));
        logger.LogInformation("Scoring for {Destination}: {Places} places, {Merged} merged, {Review} to review.", command.DestinationSlug, summary.Places, summary.AutoMerged, summary.NeedsReview);
        return Result.Success(summary);
    }

    /// <summary>An editor's decision (published, rejected, unpublished) is never overturned by a re-run; only fresh candidates move between the two open states.</summary>
    internal static PlaceStatus NextStatus(PlaceStatus current, ClassificationOutcome outcome) => current switch
    {
        PlaceStatus.Candidate or PlaceStatus.NeedsReview => outcome == ClassificationOutcome.NeedsReview ? PlaceStatus.NeedsReview : PlaceStatus.Candidate,
        _ => current,
    };

    private static async Task<int> DeduplicateAsync(string destinationSlug, IPlaceStore places, TimeProvider clock, CancellationToken cancellationToken)
    {
        var detector = new DuplicateDetector();
        var active = (await places.ListActiveAsync(destinationSlug, cancellationToken)).ToList();

        // Pairs already proposed, and pairs an editor un-merged: neither is proposed or merged again by a later run.
        var decidedPairs = (await places.ListDedupLinksAsync(destinationSlug, false, cancellationToken))
            .Where(link => link.RevertedAt is not null || !link.Automatic)
            .Select(link => PairKey(link.KeptPlaceId, link.OtherPlaceId))
            .ToHashSet();

        var merged = new HashSet<Guid>();
        var subjects = active.ToDictionary(place => place.Id, place => new DedupSubject(place.Id, place.Name, place.Location, place.Qid, place.Footprint));
        var byCell = active.GroupBy(place => Cell(place.Location.Latitude, place.Location.Longitude)).ToDictionary(group => group.Key, group => group.ToList());
        var byQid = active.Where(place => place.Qid is not null).GroupBy(place => place.Qid!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);

        // Same Wikidata item: one place, wherever the objects are.
        foreach (var group in byQid)
        {
            var ordered = group.OrderBy(place => Preference(place)).ToList();
            foreach (var other in ordered.Skip(1).Where(other => !decidedPairs.Contains(PairKey(ordered[0].Id, other.Id))))
            {
                if (merged.Add(other.Id))
                {
                    await places.MergeAsync(ordered[0].Id, other.Id, NewLink(ordered[0].Id, other.Id, detector.Compare(subjects[ordered[0].Id], subjects[other.Id]), true, clock), cancellationToken);
                }
            }
        }

        foreach (var place in active.Where(place => !merged.Contains(place.Id)))
        {
            foreach (var neighbour in Neighbours(place, byCell).Where(candidate => candidate.Id.CompareTo(place.Id) > 0 && !merged.Contains(candidate.Id) && !merged.Contains(place.Id)))
            {
                if (decidedPairs.Contains(PairKey(place.Id, neighbour.Id)) && detector.Compare(subjects[place.Id], subjects[neighbour.Id]).Verdict != DedupVerdict.Distinct)
                {
                    continue;
                }

                var decision = detector.Compare(subjects[place.Id], subjects[neighbour.Id]);
                if (decision.Verdict == DedupVerdict.AutoMerge)
                {
                    var (keep, drop) = Comparer<(int, int, Guid)>.Default.Compare(Preference(place), Preference(neighbour)) <= 0 ? (place, neighbour) : (neighbour, place);
                    merged.Add(drop.Id);
                    await places.MergeAsync(keep.Id, drop.Id, NewLink(keep.Id, drop.Id, decision, true, clock), cancellationToken);
                }
                else if (decision.Verdict == DedupVerdict.Propose && decidedPairs.Add(PairKey(place.Id, neighbour.Id)))
                {
                    await places.AddProposalAsync(NewLink(place.Id, neighbour.Id, decision, false, clock), cancellationToken);
                }
            }
        }

        return merged.Count;
    }

    /// <summary>The place that survives a merge: one with a Wikidata item, then one with a Wikipedia article, then the older record.</summary>
    private static (int, int, Guid) Preference(PlaceRecord place) =>
        (place.Qid is null ? 1 : 0, place.Enrichment?.WikipediaFr is null && place.Enrichment?.WikipediaEn is null ? 1 : 0, place.Id);

    private static IEnumerable<PlaceRecord> Neighbours(PlaceRecord place, Dictionary<(int, int), List<PlaceRecord>> byCell)
    {
        var (row, column) = Cell(place.Location.Latitude, place.Location.Longitude);
        for (var dRow = -1; dRow <= 1; dRow++)
        {
            for (var dColumn = -1; dColumn <= 1; dColumn++)
            {
                if (byCell.TryGetValue((row + dRow, column + dColumn), out var cell))
                {
                    foreach (var candidate in cell)
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static (int, int) Cell(double latitude, double longitude) => ((int)Math.Floor(latitude / CellDegrees), (int)Math.Floor(longitude / CellDegrees));

    private static string PairKey(Guid first, Guid second) => first.CompareTo(second) < 0 ? $"{first:N}:{second:N}" : $"{second:N}:{first:N}";

    private static DedupLink NewLink(Guid kept, Guid other, DedupDecision decision, bool automatic, TimeProvider clock) =>
        new(Guid.CreateVersion7(), kept, other, decision.Reason, decision.Similarity, decision.DistanceMeters, automatic, clock.GetUtcNow(), null);
}

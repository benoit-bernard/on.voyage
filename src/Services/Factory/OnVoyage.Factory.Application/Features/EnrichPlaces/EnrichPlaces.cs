using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Application.Features.EnrichPlaces;

public sealed record EnrichPlacesCommand(string DestinationSlug);

public sealed record EnrichSummary(int Requested, int Found, int WithPageviews);

public static class EnrichPlacesHandler
{
    /// <summary>Batch size of §7.3.</summary>
    public const int BatchSize = 200;

    public static async Task<(Result<EnrichSummary> Result, ScorePlacesCommand Next)> Handle(
        EnrichPlacesCommand command, IPlaceStore places, IWikidataClient wikidata, IPageviewsClient pageviews, ILogger<EnrichPlacesCommand> logger, CancellationToken cancellationToken)
    {
        var pending = (await places.ListActiveAsync(command.DestinationSlug, cancellationToken))
            .Where(place => place.Qid is not null && place.Enrichment is null)
            .Select(place => place.Qid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var found = 0;
        var withViews = 0;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            var entities = await wikidata.GetEntitiesAsync(batch, cancellationToken);
            var views = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                // French first: the app is French-first and fr.wikipedia is where Marseille is read.
                var (language, title) = entity.WikipediaFr is not null ? ("fr", entity.WikipediaFr) : entity.WikipediaEn is not null ? ("en", entity.WikipediaEn) : (null, null);
                if (language is null || title is null)
                {
                    continue;
                }

                views[entity.Qid] = await pageviews.GetAnnualViewsAsync(language, title, cancellationToken);
                withViews++;
            }

            await places.SaveEnrichmentAsync(entities, views, cancellationToken);
            found += entities.Count;
        }

        logger.LogInformation("Enrichment for {Destination}: {Requested} QIDs asked, {Found} found.", command.DestinationSlug, pending.Length, found);
        return (Result.Success(new EnrichSummary(pending.Length, found, withViews)), new ScorePlacesCommand(command.DestinationSlug));
    }
}

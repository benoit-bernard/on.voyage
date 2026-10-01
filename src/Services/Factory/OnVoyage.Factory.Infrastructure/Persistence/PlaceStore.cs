using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.IO;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Geo;
using Wolverine.EntityFrameworkCore;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsMultiPolygon = NetTopologySuite.Geometries.MultiPolygon;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed partial class PlaceStore(IDbContextOutbox<FactoryDbContext> outbox, TimeProvider clock) : IPlaceStore
{
    private FactoryDbContext Db => outbox.DbContext;

    [GeneratedRegex("^osm_place(_[0-9]{12})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RawTableName();


    public async Task<(int Created, int Updated)> UpsertFromRawAsync(string destinationSlug, string rawTable, CancellationToken cancellationToken)
    {
        if (!RawTableName().IsMatch(rawTable))
        {
            throw new ArgumentException("Unexpected raw table name.", nameof(rawTable));
        }

        // The table name is validated above; everything else is a constant.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection
        var raw = await Db.Database.SqlQueryRaw<RawOsmRowDto>($"""
            select osm_type as "OsmType", osm_id as "OsmId", name as "Name", name_en as "NameEn", wikidata as "Wikidata", version as "Version",
                   tags::text as "Tags", ST_Y(pt) as "Lat", ST_X(pt) as "Lon", ST_AsBinary(area) as "Area"
            from (select *, coalesce(geom, ST_PointOnSurface(area)) as pt from factory_raw."{rawTable}") r
            where pt is not null
            order by (wikidata is null), osm_type, osm_id
            """).ToListAsync(cancellationToken);
#pragma warning restore EF1002

        var now = clock.GetUtcNow();
        var run = new ImportRunRow { Id = Guid.CreateVersion7(), DestinationSlug = destinationSlug, RawTable = rawTable, Source = "osm", RawRows = raw.Count, StartedAt = now };

        var existing = await Db.Places.Where(place => place.DestinationSlug == destinationSlug).ToListAsync(cancellationToken);
        var byOsm = existing.ToDictionary(place => (place.OsmType, place.OsmId));
        var slugs = existing.Select(place => place.Slug).ToHashSet(StringComparer.Ordinal);
        var wkb = new WKBReader(PlaceMapper.Geometry.GeometryServices);

        int created = 0, updated = 0;
        foreach (var item in raw)
        {
            NtsMultiPolygon? area = null;
            if (item.Area is { Length: > 0 })
            {
                NtsGeometry geometry = wkb.Read(item.Area);
                geometry.SRID = PlaceMapper.Srid;
                area = geometry as NtsMultiPolygon ?? PlaceMapper.Geometry.CreateMultiPolygon([(NetTopologySuite.Geometries.Polygon)geometry]);
            }

            var qid = string.IsNullOrWhiteSpace(item.Wikidata) ? null : item.Wikidata.Trim().ToUpperInvariant();
            var location = PlaceMapper.ToPoint(new GeoPoint(item.Lat, item.Lon));
            var url = $"https://www.openstreetmap.org/{OsmUrlKind(item.OsmType)}/{item.OsmId}";

            if (byOsm.TryGetValue((item.OsmType, item.OsmId), out var current))
            {
                // Open-data columns follow the source; scores, status and editorial fields stay.
                current.Name = item.Name;
                current.NameEn = item.NameEn;
                current.Location = location;
                current.Footprint = area;
                current.Qid = current.Qid is not null && current.MergedInto is null && qid is null ? current.Qid : qid;
                current.OsmVersion = item.Version;
                current.OsmTags = item.Tags;
                current.ImportRunId = run.Id;
                current.RetrievedAt = now;
                current.UpdatedAt = now;
                updated++;
                continue;
            }

            var slug = UniqueSlug(PlaceMapper.Slugify(item.Name), item, slugs);
            Db.Places.Add(new PlaceRow
            {
                Id = Guid.CreateVersion7(),
                DestinationSlug = destinationSlug,
                Slug = slug,
                Name = item.Name,
                NameEn = item.NameEn,
                Location = location,
                Footprint = area,
                Qid = qid,
                OsmType = item.OsmType,
                OsmId = item.OsmId,
                OsmVersion = item.Version,
                OsmTags = item.Tags,
                Status = nameof(PlaceStatus.Candidate),
                SourceUrl = url,
                ImportRunId = run.Id,
                RetrievedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            created++;
        }

        run.Created = created;
        run.Updated = updated;
        Db.ImportRuns.Add(run);
        await Db.SaveChangesAsync(cancellationToken);
        return (created, updated);
    }

    private sealed class RawOsmRowDto
    {
        public string OsmType { get; set; } = string.Empty;
        public long OsmId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameEn { get; set; }
        public string? Wikidata { get; set; }
        public int? Version { get; set; }
        public string Tags { get; set; } = "{}";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public byte[]? Area { get; set; }
    }

    private static string OsmUrlKind(string osmType) => osmType switch { "N" => "node", "W" => "way", _ => "relation" };

    private static string UniqueSlug(string slug, RawOsmRowDto item, HashSet<string> taken)
    {
        var candidate = slug;
        if (taken.Contains(candidate))
        {
            candidate = $"{slug}-{char.ToLowerInvariant(item.OsmType[0])}{item.OsmId}";
        }

        taken.Add(candidate);
        return candidate;
    }

    public async Task<IReadOnlyList<PlaceRecord>> ListActiveAsync(string destinationSlug, CancellationToken cancellationToken)
    {
        var merged = nameof(PlaceStatus.Merged);
        var rejected = nameof(PlaceStatus.Rejected);
        var rows = await Db.Places.AsNoTracking().Where(place => place.DestinationSlug == destinationSlug && place.Status != merged && place.Status != rejected).ToListAsync(cancellationToken);
        return await ToRecordsAsync(rows, cancellationToken);
    }

    public async Task<PlaceRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await Db.Places.AsNoTracking().FirstOrDefaultAsync(place => place.Id == id, cancellationToken);
        return row is null ? null : (await ToRecordsAsync([row], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<PlaceRecord>> ListAsync(string destinationSlug, PlaceStatus? status, int limit, CancellationToken cancellationToken)
    {
        var query = Db.Places.AsNoTracking().Where(place => place.DestinationSlug == destinationSlug);
        if (status is { } wanted)
        {
            var name = wanted.ToString();
            query = query.Where(place => place.Status == name);
        }

        var rows = await query.OrderByDescending(place => place.ImportanceScore).ThenBy(place => place.Slug).Take(limit).ToListAsync(cancellationToken);
        return await ToRecordsAsync(rows, cancellationToken);
    }

    private async Task<IReadOnlyList<PlaceRecord>> ToRecordsAsync(List<PlaceRow> rows, CancellationToken cancellationToken)
    {
        var qids = rows.Where(row => row.Qid is not null).Select(row => row.Qid!).Distinct().ToArray();
        var entities = qids.Length == 0
            ? []
            : await Db.WikidataEntities.AsNoTracking().Where(entity => qids.Contains(entity.Qid)).ToDictionaryAsync(entity => entity.Qid, cancellationToken);
        return [.. rows.Select(row => PlaceMapper.ToRecord(row, row.Qid is not null && entities.TryGetValue(row.Qid, out var entity) ? entity : null))];
    }

    public async Task SaveEnrichmentAsync(IReadOnlyList<PlaceEnrichment> enrichments, IReadOnlyDictionary<string, long> annualPageviewsByQid, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var enrichment in enrichments)
        {
            var row = await Db.WikidataEntities.FirstOrDefaultAsync(entity => entity.Qid == enrichment.Qid, cancellationToken);
            if (row is null)
            {
                row = new WikidataEntityRow { Qid = enrichment.Qid };
                Db.WikidataEntities.Add(row);
            }

            row.LabelFr = enrichment.LabelFr;
            row.LabelEn = enrichment.LabelEn;
            row.DescriptionFr = enrichment.DescriptionFr;
            row.DescriptionEn = enrichment.DescriptionEn;
            row.InstanceOf = [.. enrichment.InstanceOf];
            row.HeritageStatuses = [.. enrichment.HeritageStatuses];
            row.Inception = enrichment.Inception;
            row.Sitelinks = enrichment.Sitelinks;
            row.WikipediaFr = enrichment.WikipediaFr;
            row.WikipediaEn = enrichment.WikipediaEn;
            row.Image = enrichment.Image;
            row.Website = enrichment.Website;
            row.SourceUrl = $"https://www.wikidata.org/wiki/{enrichment.Qid}";
            row.RetrievedAt = enrichment.RetrievedAt;
        }

        foreach (var (qid, views) in annualPageviewsByQid)
        {
            var entity = enrichments.FirstOrDefault(item => string.Equals(item.Qid, qid, StringComparison.OrdinalIgnoreCase));
            var (language, title) = entity?.WikipediaFr is not null ? ("fr", entity.WikipediaFr) : ("en", entity?.WikipediaEn ?? string.Empty);
            var pageviews = await Db.Pageviews.FirstOrDefaultAsync(item => item.Qid == qid && item.Language == language, cancellationToken);
            if (pageviews is null)
            {
                pageviews = new PageviewsRow { Qid = qid, Language = language };
                Db.Pageviews.Add(pageviews);
            }

            pageviews.Title = title;
            pageviews.Views12Months = views;
            pageviews.RetrievedAt = now;
        }

        await Db.SaveChangesAsync(cancellationToken);

        // Denormalised onto the place: the scorer reads one number per place.
        foreach (var (qid, views) in annualPageviewsByQid)
        {
            await Db.Places.Where(place => place.Qid == qid).ExecuteUpdateAsync(update => update.SetProperty(place => place.AnnualPageviews, views), cancellationToken);
        }
    }

    public async Task SaveScoringAsync(IReadOnlyList<PlaceScoring> scorings, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ids = scorings.Select(scoring => scoring.PlaceId).ToArray();
        var rows = await Db.Places.Include(place => place.Interests).Where(place => ids.Contains(place.Id)).ToDictionaryAsync(place => place.Id, cancellationToken);

        foreach (var scoring in scorings)
        {
            if (!rows.TryGetValue(scoring.PlaceId, out var row))
            {
                continue;
            }

            row.ImportanceScore = (short)scoring.Importance;
            row.PopularityPercentile = (short)scoring.Percentile;
            row.HiddenGem = scoring.HiddenGem;
            row.CrowdOffpeak = (short)scoring.Crowd.Offpeak;
            row.CrowdShoulder = (short)scoring.Crowd.Shoulder;
            row.CrowdPeak = (short)scoring.Crowd.Peak;
            row.ClassificationOutcome = scoring.Outcome.ToString();
            row.ClassificationConfidence = (float)scoring.Confidence;
            row.Status = scoring.Status.ToString();
            row.UpdatedAt = now;

            // An editor's weights are never overwritten by a re-run; the others follow the new vector (update in place, add, remove).
            var source = scoring.Outcome == OnVoyage.Factory.Domain.Classification.ClassificationOutcome.Model ? "model" : "rule";
            var wanted = scoring.Vector.Weights;
            foreach (var interest in row.Interests.Where(interest => interest.Source != "editor").ToList())
            {
                if (wanted.TryGetValue(interest.TaxonomyCode, out var weight))
                {
                    interest.Weight = (float)weight;
                    interest.Source = source;
                }
                else
                {
                    Db.PlaceInterests.Remove(interest);
                }
            }

            var present = row.Interests.Select(interest => interest.TaxonomyCode).ToHashSet(StringComparer.Ordinal);
            foreach (var (code, weight) in wanted.Where(pair => !present.Contains(pair.Key)))
            {
                Db.PlaceInterests.Add(new PlaceInterestRow { PlaceId = row.Id, TaxonomyCode = code, Weight = (float)weight, Source = source });
            }
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(string Code, double Weight)>> GetInterestsAsync(Guid placeId, CancellationToken cancellationToken) =>
        [.. (await Db.PlaceInterests.AsNoTracking().Where(interest => interest.PlaceId == placeId).OrderBy(interest => interest.TaxonomyCode).ToListAsync(cancellationToken))
            .Select(interest => (interest.TaxonomyCode, (double)interest.Weight))];

    public async Task<PlaceScoreDetail> GetScoreDetailAsync(Guid placeId, CancellationToken cancellationToken)
    {
        var row = await Db.Places.AsNoTracking().FirstAsync(place => place.Id == placeId, cancellationToken);
        return new PlaceScoreDetail(row.ImportanceScore ?? 0, row.PopularityPercentile ?? 0, row.HiddenGem, row.CrowdOffpeak, row.CrowdShoulder, row.CrowdPeak, row.Fragile, row.AccessRegulated);
    }

    public async Task MergeAsync(Guid keptPlaceId, Guid otherPlaceId, DedupLink link, CancellationToken cancellationToken)
    {
        var kept = await Db.Places.FirstAsync(place => place.Id == keptPlaceId, cancellationToken);
        var other = await Db.Places.FirstAsync(place => place.Id == otherPlaceId, cancellationToken);

        string? inherited = null;
        if (kept.Qid is null && other.Qid is not null)
        {
            kept.Qid = other.Qid;
            inherited = other.Qid;
        }

        other.MergedInto = kept.Id;
        other.Status = nameof(PlaceStatus.Merged);
        other.UpdatedAt = clock.GetUtcNow();

        var existing = await Db.DedupLinks.FirstOrDefaultAsync(row => row.Id == link.Id, cancellationToken);
        if (existing is null)
        {
            Db.DedupLinks.Add(ToRow(link, inherited, "merged"));
        }
        else
        {
            existing.State = "merged";
            existing.InheritedQid = inherited;
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddProposalAsync(DedupLink link, CancellationToken cancellationToken)
    {
        Db.DedupLinks.Add(ToRow(link, null, "proposed"));
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DedupLink>> ListDedupLinksAsync(string destinationSlug, bool proposalsOnly, CancellationToken cancellationToken)
    {
        var rows = await (from link in Db.DedupLinks.AsNoTracking()
                          join place in Db.Places.AsNoTracking() on link.KeptPlaceId equals place.Id
                          where place.DestinationSlug == destinationSlug && (!proposalsOnly || (link.State == "proposed" && link.RevertedAt == null))
                          select link).ToListAsync(cancellationToken);
        return [.. rows.Select(ToLink)];
    }

    public async Task<IReadOnlyList<DedupLink>> ListAllDedupLinksAsync(CancellationToken cancellationToken) =>
        [.. (await Db.DedupLinks.AsNoTracking().ToListAsync(cancellationToken)).Select(ToLink)];

    public async Task<bool> RevertMergeAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var link = await Db.DedupLinks.FirstOrDefaultAsync(row => row.Id == linkId && row.State == "merged" && row.RevertedAt == null, cancellationToken);
        if (link is null)
        {
            return false;
        }

        var kept = await Db.Places.FirstAsync(place => place.Id == link.KeptPlaceId, cancellationToken);
        var other = await Db.Places.FirstAsync(place => place.Id == link.OtherPlaceId, cancellationToken);
        if (link.InheritedQid is not null && string.Equals(kept.Qid, link.InheritedQid, StringComparison.OrdinalIgnoreCase))
        {
            kept.Qid = null;
        }

        other.MergedInto = null;
        other.Status = nameof(PlaceStatus.Candidate);
        other.UpdatedAt = clock.GetUtcNow();
        link.RevertedAt = clock.GetUtcNow();
        link.State = "reverted";
        await Db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetEditorialAsync(Guid placeId, int? importanceOverride, bool? saturated, CancellationToken cancellationToken)
    {
        var row = await Db.Places.FirstAsync(place => place.Id == placeId, cancellationToken);
        row.ImportanceOverride = (short?)importanceOverride;
        if (saturated is { } value)
        {
            row.EditoriallySaturated = value;
        }

        row.UpdatedAt = clock.GetUtcNow();
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid placeId, PlaceStatus status, CancellationToken cancellationToken) =>
        await Db.Places.Where(place => place.Id == placeId).ExecuteUpdateAsync(update => update.SetProperty(place => place.Status, status.ToString()).SetProperty(place => place.UpdatedAt, clock.GetUtcNow()), cancellationToken);

    public async Task PublishAsync(Guid placeId, int version, object integrationEvent, CancellationToken cancellationToken)
    {
        var row = await Db.Places.FirstAsync(place => place.Id == placeId, cancellationToken);
        row.Status = nameof(PlaceStatus.Published);
        row.PublishedVersion = version;
        row.UpdatedAt = clock.GetUtcNow();
        await outbox.PublishAsync(integrationEvent);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task UnpublishAsync(Guid placeId, int version, object integrationEvent, CancellationToken cancellationToken)
    {
        var row = await Db.Places.FirstAsync(place => place.Id == placeId, cancellationToken);
        row.Status = nameof(PlaceStatus.Unpublished);
        row.PublishedVersion = version;
        row.UpdatedAt = clock.GetUtcNow();
        await outbox.PublishAsync(integrationEvent);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private static DedupLinkRow ToRow(DedupLink link, string? inherited, string state) => new()
    {
        Id = link.Id,
        KeptPlaceId = link.KeptPlaceId,
        OtherPlaceId = link.OtherPlaceId,
        Reason = link.Reason,
        Similarity = link.Similarity,
        DistanceMeters = link.DistanceMeters,
        Automatic = link.Automatic,
        InheritedQid = inherited,
        CreatedAt = link.CreatedAt,
        State = state,
    };

    private static DedupLink ToLink(DedupLinkRow row) =>
        new(row.Id, row.KeptPlaceId, row.OtherPlaceId, row.Reason, row.Similarity, row.DistanceMeters, row.Automatic, row.CreatedAt, row.RevertedAt);
}

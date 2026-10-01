using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Infrastructure.Sources;

/// <summary>Shared etiquette for Wikimedia APIs (§7.1): the mandatory User-Agent, one request at a time, exponential back-off on 429.</summary>
internal sealed class WikimediaRequester(HttpClient http, TimeProvider clock, ILogger<WikimediaRequester> logger)
{
    public const string UserAgent = "OnVoyageBot/1.0 (https://on.voyage/bot; contact@on.voyage)";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public int MaxAttempts { get; init; } = 5;

    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<string?> GetStringAsync(Func<HttpRequestMessage> build, string accept, CancellationToken cancellationToken)
    {
        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            var delay = FirstDelay;
            for (var attempt = 1; ; attempt++)
            {
                using var request = build();
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxAttempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? delay;
                    logger.LogWarning("Wikimedia asked to slow down (attempt {Attempt}); waiting {Seconds} s.", attempt, (int)wait.TotalSeconds);
                    await Task.Delay(wait, clock, cancellationToken);
                    delay *= 2;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}

/// <summary>Reads entities in one SPARQL query per batch (§7.3). The query returns one row per fact, which is parsed back by property.</summary>
internal sealed class WikidataSparqlClient(WikimediaRequester requester, TimeProvider clock) : IWikidataClient
{
    public const string Endpoint = "https://query.wikidata.org/sparql";

    public async Task<IReadOnlyList<PlaceEnrichment>> GetEntitiesAsync(IReadOnlyList<string> qids, CancellationToken cancellationToken)
    {
        var valid = qids.Where(IsQid).Select(qid => qid.ToUpperInvariant()).Distinct().ToArray();
        if (valid.Length == 0)
        {
            return [];
        }

        var query = BuildQuery(valid);
        var body = await requester.GetStringAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}?format=json&query={Uri.EscapeDataString(query)}"),
            "application/sparql-results+json",
            cancellationToken);

        return body is null ? [] : Parse(body, clock.GetUtcNow());
    }

    internal static bool IsQid(string value) => value.Length is > 1 and < 16 && (value[0] is 'Q' or 'q') && value.Skip(1).All(char.IsAsciiDigit);

    internal static string BuildQuery(IEnumerable<string> qids)
    {
        var values = string.Join(' ', qids.Select(qid => $"wd:{qid}"));
        var builder = new StringBuilder();
        builder.Append("SELECT ?item ?kind ?value WHERE { VALUES ?item { ").Append(values).Append(" } ");
        builder.Append("""
            { ?item rdfs:label ?value . FILTER(LANG(?value) = "fr") BIND("label_fr" AS ?kind) }
            UNION { ?item rdfs:label ?value . FILTER(LANG(?value) = "en") BIND("label_en" AS ?kind) }
            UNION { ?item schema:description ?value . FILTER(LANG(?value) = "fr") BIND("description_fr" AS ?kind) }
            UNION { ?item schema:description ?value . FILTER(LANG(?value) = "en") BIND("description_en" AS ?kind) }
            UNION { ?item wdt:P31 ?class . BIND(STR(?class) AS ?value) BIND("instance_of" AS ?kind) }
            UNION { ?item wdt:P31/wdt:P279 ?class . BIND(STR(?class) AS ?value) BIND("instance_of" AS ?kind) }
            UNION { ?item wdt:P31/wdt:P279/wdt:P279 ?class . BIND(STR(?class) AS ?value) BIND("instance_of" AS ?kind) }
            UNION { ?item wdt:P1435 ?status . BIND(STR(?status) AS ?value) BIND("heritage" AS ?kind) }
            UNION { ?item wdt:P571 ?inception . BIND(STR(?inception) AS ?value) BIND("inception" AS ?kind) }
            UNION { ?item wdt:P18 ?image . BIND(STR(?image) AS ?value) BIND("image" AS ?kind) }
            UNION { ?item wdt:P856 ?site . BIND(STR(?site) AS ?value) BIND("website" AS ?kind) }
            UNION { ?item wikibase:sitelinks ?sitelinks . BIND(STR(?sitelinks) AS ?value) BIND("sitelinks" AS ?kind) }
            UNION { ?article schema:about ?item ; schema:isPartOf <https://fr.wikipedia.org/> ; schema:name ?value . BIND("wikipedia_fr" AS ?kind) }
            UNION { ?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> ; schema:name ?value . BIND("wikipedia_en" AS ?kind) }
            }
            """);
        return builder.ToString();
    }

    internal static IReadOnlyList<PlaceEnrichment> Parse(string json, DateTimeOffset retrievedAt)
    {
        using var document = JsonDocument.Parse(json);
        var facts = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in document.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            var item = row.GetProperty("item").GetProperty("value").GetString()!;
            var qid = item[(item.LastIndexOf('/') + 1)..];
            var kind = row.GetProperty("kind").GetProperty("value").GetString()!;
            var value = row.GetProperty("value").GetProperty("value").GetString()!;

            if (!facts.TryGetValue(qid, out var byKind))
            {
                facts[qid] = byKind = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }

            if (!byKind.TryGetValue(kind, out var list))
            {
                byKind[kind] = list = [];
            }

            list.Add(kind is "instance_of" or "heritage" ? value[(value.LastIndexOf('/') + 1)..] : value);
        }

        return [.. facts.Select(pair => Build(pair.Key.ToUpperInvariant(), pair.Value, retrievedAt))];
    }

    private static PlaceEnrichment Build(string qid, Dictionary<string, List<string>> facts, DateTimeOffset retrievedAt)
    {
        string? First(string kind) => facts.TryGetValue(kind, out var list) ? list.OrderBy(value => value, StringComparer.Ordinal).First() : null;
        string[] All(string kind) => facts.TryGetValue(kind, out var list) ? [.. list.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : [];

        return new PlaceEnrichment(
            qid,
            First("label_fr"),
            First("label_en"),
            First("description_fr"),
            First("description_en"),
            All("instance_of"),
            All("heritage"),
            First("inception"),
            int.TryParse(First("sitelinks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sitelinks) ? sitelinks : 0,
            First("wikipedia_fr"),
            First("wikipedia_en"),
            First("image"),
            First("website"),
            retrievedAt);
    }
}

/// <summary>Monthly page views of the last twelve full months from the Wikimedia REST API.</summary>
internal sealed class WikimediaPageviewsClient(WikimediaRequester requester, TimeProvider clock) : IPageviewsClient
{
    public const string Endpoint = "https://wikimedia.org/api/rest_v1/metrics/pageviews/per-article";

    public async Task<long> GetAnnualViewsAsync(string language, string title, CancellationToken cancellationToken)
    {
        if (language is not ("fr" or "en") || string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var (start, end) = Window(clock.GetUtcNow());
        var article = Uri.EscapeDataString(title.Replace(' ', '_'));
        var body = await requester.GetStringAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}/{language}.wikipedia/all-access/user/{article}/monthly/{start}/{end}"),
            "application/json",
            cancellationToken);

        return body is null ? 0 : SumViews(body);
    }

    /// <summary>First day of the month twelve months ago to first day of last month: twelve complete months.</summary>
    internal static (string Start, string End) Window(DateTimeOffset now)
    {
        var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (firstOfThisMonth.AddMonths(-12).ToString("yyyyMM'01'00", CultureInfo.InvariantCulture), firstOfThisMonth.AddMonths(-1).ToString("yyyyMM'01'00", CultureInfo.InvariantCulture));
    }

    internal static long SumViews(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Sum(item => item.GetProperty("views").GetInt64()) : 0;
    }
}

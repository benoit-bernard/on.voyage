using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Tests.Discovery;

/// <summary>
/// The "expected places" of a trace (H-001, §20.4): <c>data-pipeline/gpx/&lt;trace&gt;.expected-places.json</c>, written by hand after the field recording.
/// Format and rules: <c>data-pipeline/gpx/expected-places.schema.json</c> and <c>docs/field/h001-traces-gpx.md</c>.
/// </summary>
internal sealed record ExpectedPlaces(
    int SchemaVersion,
    string Trace,
    string Scenario,
    string Status,
    bool Synthetic,
    string? Candidates,
    TravelMode? ExpectedMode,
    int? MinTriggers,
    int? MaxTriggers,
    IReadOnlyList<ExpectedPlaces.Expectation>? MustTrigger,
    IReadOnlyList<ExpectedPlaces.Forbidden>? MustNotTrigger,
    string? Notes)
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Suffix = ".expected-places.json";
    public static readonly IReadOnlyList<string> Scenarios = ["walk", "bike", "car50", "highway110", "tunnel", "canyon"];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public sealed record Expectation(string Place, string Reason, double? AfterSeconds, double? BeforeSeconds);

    public sealed record Forbidden(string Place, string Reason);

    public string CandidatesFile => Candidates ?? "candidates.json";

    public static ExpectedPlaces Load(string path)
    {
        // "$schema" is the only member we ignore: editors use it, the test does not.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("$schema");
        return node.Deserialize<ExpectedPlaces>(Options) ?? throw new InvalidDataException($"{path} is empty.");
    }

    /// <summary>Everything that can be checked without replaying: names, references, consistency. Returns the problems (empty = valid).</summary>
    public IReadOnlyList<string> Validate(string expectedTrace, bool traceIsSynthetic, IReadOnlyList<GpxReader.Place> candidates)
    {
        List<string> problems = [];
        if (SchemaVersion != 1)
        {
            problems.Add($"schemaVersion must be 1 (got {SchemaVersion}).");
        }

        if (Trace != expectedTrace)
        {
            problems.Add($"trace is '{Trace}' but the file is for '{expectedTrace}'.");
        }

        if (!Scenarios.Contains(Scenario))
        {
            problems.Add($"scenario '{Scenario}' is unknown (expected one of {string.Join(", ", Scenarios)}).");
        }

        if (Status is not (Draft or Approved))
        {
            problems.Add($"status must be '{Draft}' or '{Approved}' (got '{Status}').");
        }

        if (Synthetic != traceIsSynthetic)
        {
            problems.Add($"synthetic is {Synthetic.ToString().ToLowerInvariant()} but the creator of the GPX file says {(traceIsSynthetic ? "synthetic" : "real")}.");
        }

        var names = candidates.Select(place => place.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expectation in MustTrigger ?? [])
        {
            if (!names.Contains(expectation.Place))
            {
                problems.Add($"mustTrigger: unknown place '{expectation.Place}' (not in {CandidatesFile}).");
            }

            if (string.IsNullOrWhiteSpace(expectation.Reason))
            {
                problems.Add($"mustTrigger '{expectation.Place}': give the reason (what you saw or heard on the spot).");
            }

            if (expectation is { AfterSeconds: { } after, BeforeSeconds: { } before } && after >= before)
            {
                problems.Add($"mustTrigger '{expectation.Place}': afterSeconds must be smaller than beforeSeconds.");
            }
        }

        foreach (var forbidden in MustNotTrigger ?? [])
        {
            if (!names.Contains(forbidden.Place))
            {
                problems.Add($"mustNotTrigger: unknown place '{forbidden.Place}' (not in {CandidatesFile}).");
            }

            if (string.IsNullOrWhiteSpace(forbidden.Reason))
            {
                problems.Add($"mustNotTrigger '{forbidden.Place}': give the reason.");
            }
        }

        var both = (MustTrigger ?? []).Select(e => e.Place).Intersect((MustNotTrigger ?? []).Select(f => f.Place));
        problems.AddRange(both.Select(place => $"'{place}' is both in mustTrigger and in mustNotTrigger."));
        if (MinTriggers is { } min && MaxTriggers is { } max && min > max)
        {
            problems.Add("minTriggers is greater than maxTriggers.");
        }

        if (Status == Approved && (MustTrigger ?? []).Count == 0 && (MustNotTrigger ?? []).Count == 0 && MaxTriggers is null)
        {
            problems.Add("an approved file must say something: mustTrigger, mustNotTrigger or maxTriggers.");
        }

        return problems;
    }

    /// <summary>Compares the replay with the expectations. Returns the differences (empty = the engine behaves as the field recording says).</summary>
    public IReadOnlyList<string> Compare(ReplayResult result)
    {
        List<string> differences = [];
        foreach (var expectation in MustTrigger ?? [])
        {
            var told = result.Triggers.Where(trigger => trigger.Place == expectation.Place).ToList();
            if (told.Count == 0)
            {
                differences.Add($"'{expectation.Place}' should have been triggered ({expectation.Reason}) but was not.");
                continue;
            }

            if (!told.Any(trigger => (expectation.AfterSeconds is not { } after || trigger.AtSeconds >= after) && (expectation.BeforeSeconds is not { } before || trigger.AtSeconds <= before)))
            {
                differences.Add($"'{expectation.Place}' was triggered at {string.Join(", ", told.Select(t => $"{t.AtSeconds:0}s"))} outside [{expectation.AfterSeconds?.ToString("0", CultureInfo.InvariantCulture) ?? "start"} s, {expectation.BeforeSeconds?.ToString("0", CultureInfo.InvariantCulture) ?? "end"} s].");
            }
        }

        foreach (var forbidden in MustNotTrigger ?? [])
        {
            if (result.Triggers.Any(trigger => trigger.Place == forbidden.Place))
            {
                differences.Add($"'{forbidden.Place}' must not be triggered ({forbidden.Reason}) but was.");
            }
        }

        if (MinTriggers is { } min && result.Triggers.Count < min)
        {
            differences.Add($"{result.Triggers.Count} trigger(s), at least {min} expected.");
        }

        if (MaxTriggers is { } max && result.Triggers.Count > max)
        {
            differences.Add($"{result.Triggers.Count} trigger(s), at most {max} expected.");
        }

        if (ExpectedMode is { } mode)
        {
            var wrong = result.Triggers.Where(trigger => trigger.Mode != mode).Select(trigger => $"{trigger.Place} ({trigger.Mode})").ToList();
            if (wrong.Count > 0)
            {
                differences.Add($"expected mode {mode}, got: {string.Join(", ", wrong)}.");
            }
        }

        return differences;
    }
}

/// <summary>Finds the traces and their expected-places files in the folder copied next to the test assembly.</summary>
internal static class GpxCorpus
{
    public static IEnumerable<string> Traces() =>
        Directory.GetFiles(GpxReader.TraceDirectory, "*.gpx").Select(path => Path.GetFileNameWithoutExtension(path)!).OrderBy(name => name, StringComparer.Ordinal);

    public static string ExpectedPath(string trace) => Path.Combine(GpxReader.TraceDirectory, trace + ExpectedPlaces.Suffix);

    public static IEnumerable<string> ExpectedFiles() =>
        Directory.GetFiles(GpxReader.TraceDirectory, "*" + ExpectedPlaces.Suffix).Select(path => Path.GetFileName(path)![..^ExpectedPlaces.Suffix.Length]).OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>A trace is synthetic when its <c>creator</c> attribute says so (<c>generate_synthetic.py</c>); every other trace is a field recording.</summary>
    public static bool IsSynthetic(string trace)
    {
        var creator = XDocument.Load(Path.Combine(GpxReader.TraceDirectory, trace + ".gpx")).Root?.Attribute("creator")?.Value ?? string.Empty;
        return creator.Contains("synthetic", StringComparison.OrdinalIgnoreCase);
    }
}

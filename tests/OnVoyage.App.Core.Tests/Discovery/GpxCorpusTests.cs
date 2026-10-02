using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Tests.Discovery;

/// <summary>
/// Replays every trace of <c>data-pipeline/gpx</c> against its <c>*.expected-places.json</c> (H-001, §20.4). Dropping a real recording and its
/// expected-places file in the folder is enough: no test code to write. Synthetic traces (creator "synthetic") may have no expected file;
/// a real recording without one fails, so that H-001 cannot be half done.
/// </summary>
public sealed class GpxCorpusTests
{
    public static TheoryData<string> Traces => [.. GpxCorpus.Traces()];

    public static TheoryData<string> TracesWithExpectations => [.. GpxCorpus.ExpectedFiles()];

    [Theory]
    [MemberData(nameof(Traces))]
    public void Every_trace_is_a_readable_ordered_track_and_a_real_one_comes_with_its_expected_places(string trace)
    {
        var fixes = GpxReader.Read(trace);

        fixes.Count.ShouldBeGreaterThan(20);
        for (var i = 1; i < fixes.Count; i++)
        {
            fixes[i].Timestamp.ShouldBeGreaterThan(fixes[i - 1].Timestamp, $"{trace}: time must increase strictly (point {i})");
        }

        if (!GpxCorpus.IsSynthetic(trace))
        {
            File.Exists(GpxCorpus.ExpectedPath(trace)).ShouldBeTrue(
                $"{trace}.gpx is a real recording: add {trace}{ExpectedPlaces.Suffix} next to it (template: expected-places.template.json, docs/field/h001-traces-gpx.md).");
        }
    }

    [Theory]
    [MemberData(nameof(TracesWithExpectations))]
    public void Every_expected_places_file_describes_an_existing_trace_and_is_consistent(string trace)
    {
        File.Exists(Path.Combine(GpxReader.TraceDirectory, trace + ".gpx")).ShouldBeTrue($"{trace}{ExpectedPlaces.Suffix} has no {trace}.gpx");

        var expected = ExpectedPlaces.Load(GpxCorpus.ExpectedPath(trace));
        File.Exists(Path.Combine(GpxReader.TraceDirectory, expected.CandidatesFile)).ShouldBeTrue($"{trace}: candidates file '{expected.CandidatesFile}' not found");

        expected.Validate(trace, GpxCorpus.IsSynthetic(trace), GpxReader.ReadPlaces(expected.CandidatesFile)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(TracesWithExpectations))]
    public void The_discovery_engine_triggers_the_expected_places_on_every_approved_trace(string trace)
    {
        var expected = ExpectedPlaces.Load(GpxCorpus.ExpectedPath(trace));
        if (expected.Status != ExpectedPlaces.Approved)
        {
            return; // draft: the human is still working on it; structure is checked by the test above, behaviour is not asserted yet
        }

        var result = Replay.Run(trace, candidatesFile: expected.CandidatesFile);

        expected.Compare(result).ShouldBeEmpty($"{trace}: the engine and the field recording disagree. Triggered: {string.Join(", ", result.Triggers.Select(t => $"{t.Place}@{t.AtSeconds:0}s"))}");
    }

    [Fact]
    public void The_comparison_reports_a_missing_a_forbidden_and_a_badly_timed_place()
    {
        var expected = new ExpectedPlaces(
            1, "walk_vieux_port", "walk", ExpectedPlaces.Approved, true, null, TravelMode.Bike, 10, null,
            [new("Mucem", "seen the museum", null, null), new("Hôtel de Ville", "late", 0, 100), new("Fort Saint-Jean", "not reached", null, null)],
            [new("Vieille Charité", "closed street")], null);

        var differences = expected.Compare(Replay.Run("walk_vieux_port"));

        differences.ShouldContain(difference => difference.Contains("'Fort Saint-Jean' should have been triggered", StringComparison.Ordinal));
        differences.ShouldContain(difference => difference.Contains("'Hôtel de Ville' was triggered at", StringComparison.Ordinal));
        differences.ShouldContain(difference => difference.Contains("'Vieille Charité' must not be triggered", StringComparison.Ordinal));
        differences.ShouldContain(difference => difference.Contains("at least 10 expected", StringComparison.Ordinal));
        differences.ShouldContain(difference => difference.Contains("expected mode Bike", StringComparison.Ordinal));
        differences.ShouldNotContain(difference => difference.Contains("'Mucem'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_catches_typos_wrong_trace_and_contradictions()
    {
        var places = GpxReader.ReadPlaces();
        var expected = new ExpectedPlaces(
            2, "other", "bicycle", "maybe", false, null, null, 5, 3,
            [new("Mucemm", "", 50, 10), new("Mucem", "x", null, null)], [new("Mucem", "x")], null);

        var problems = expected.Validate("walk_vieux_port", true, places);

        problems.Count.ShouldBeGreaterThanOrEqualTo(9);
        problems.ShouldContain(problem => problem.Contains("unknown place 'Mucemm'", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("both in mustTrigger and in mustNotTrigger", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("scenario 'bicycle'", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("synthetic is false", StringComparison.Ordinal));
    }

    [Fact]
    public void The_template_and_the_schema_ship_with_the_traces_and_the_template_loads_as_a_draft()
    {
        File.Exists(Path.Combine(GpxReader.TraceDirectory, "expected-places.schema.json")).ShouldBeTrue();
        var template = ExpectedPlaces.Load(Path.Combine(GpxReader.TraceDirectory, "expected-places.template.json"));

        template.Status.ShouldBe(ExpectedPlaces.Draft);
        template.SchemaVersion.ShouldBe(1);
    }
}

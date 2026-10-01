using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Scoring;

namespace Factory.UnitTests;

public sealed class ScoringTests
{
    private static TaxonomyVector Vector(params (string Code, double Weight)[] weights) => TaxonomyVector.From(weights.Select(w => new KeyValuePair<string, double>(w.Code, w.Weight)));

    [Fact]
    public void Importance_adds_the_documented_contributions()
    {
        // UNESCO 40 + classified 30 + min(25, 8·log2(1+1023)=80)=25 + min(20, 4·log10(1+999999)=24)=20 + attraction 10 = 125, capped at 100.
        ImportanceScorer.Score(new ImportanceInput(true, HeritageStatus.Classified, 1023, 999_999, true)).ShouldBe(100);
    }

    [Theory]
    [InlineData(false, HeritageStatus.None, 0, 0, false, 0)]
    [InlineData(false, HeritageStatus.Inscribed, 0, 0, false, 20)]
    [InlineData(false, HeritageStatus.Classified, 0, 0, false, 30)]
    [InlineData(true, HeritageStatus.None, 0, 0, false, 40)]
    [InlineData(false, HeritageStatus.None, 0, 0, true, 10)]
    [InlineData(false, HeritageStatus.None, 1, 0, false, 8)]
    [InlineData(false, HeritageStatus.None, 3, 0, false, 16)]
    [InlineData(false, HeritageStatus.None, 0, 9999, false, 16)]
    public void Each_term_counts_on_its_own(bool unesco, HeritageStatus heritage, int sitelinks, long views, bool attraction, int expected) =>
        ImportanceScorer.Score(new ImportanceInput(unesco, heritage, sitelinks, views, attraction)).ShouldBe(expected);

    [Fact]
    public void Sitelinks_and_pageviews_are_capped_each()
    {
        ImportanceScorer.Score(new ImportanceInput(false, HeritageStatus.None, 1_000_000, 0, false)).ShouldBe(25);
        ImportanceScorer.Score(new ImportanceInput(false, HeritageStatus.None, 0, 100_000_000_000, false)).ShouldBe(20);
    }

    [Fact]
    public void An_editorial_override_wins_and_is_clamped()
    {
        ImportanceScorer.Score(new ImportanceInput(true, HeritageStatus.Classified, 100, 1_000_000, true, 12)).ShouldBe(12);
        ImportanceScorer.Score(new ImportanceInput(false, HeritageStatus.None, 0, 0, false, 250)).ShouldBe(100);
    }

    [Fact]
    public void Negative_inputs_cannot_lower_the_score()
    {
        ImportanceScorer.Score(new ImportanceInput(false, HeritageStatus.None, -5, -100, false)).ShouldBe(0);
    }

    [Fact]
    public void Percentiles_rank_places_by_page_views_within_the_destination()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var views = ids.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => (long)(item.index * 100));

        var percentiles = PopularityPercentiles.Compute(views);

        percentiles[ids[0]].ShouldBe(0);
        percentiles[ids[1]].ShouldBe(10);
        percentiles[ids[5]].ShouldBe(50);
        percentiles[ids[9]].ShouldBe(90);
    }

    [Fact]
    public void Ties_share_a_percentile_and_unread_places_sit_at_zero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var percentiles = PopularityPercentiles.Compute(new Dictionary<Guid, long> { [a] = 500, [b] = 500, [c] = 0 });

        percentiles[a].ShouldBe(percentiles[b]);
        percentiles[c].ShouldBe(0);
        PopularityPercentiles.Compute(new Dictionary<Guid, long>()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(96, 5)]
    [InlineData(95, 5)]
    [InlineData(94, 4)]
    [InlineData(80, 4)]
    [InlineData(79, 3)]
    [InlineData(50, 3)]
    [InlineData(49, 2)]
    [InlineData(20, 2)]
    [InlineData(19, 1)]
    [InlineData(0, 1)]
    public void The_base_crowd_level_follows_the_percentile(int percentile, int expectedShoulder)
    {
        var crowd = CrowdProfileBuilder.Build(percentile, Vector(("history.military", 0.9)), false);

        crowd.Shoulder.ShouldBe(expectedShoulder);
        crowd.Peak.ShouldBe(expectedShoulder);
        crowd.Offpeak.ShouldBe(Math.Max(1, expectedShoulder - 1));
    }

    [Fact]
    public void Beaches_calanques_viewpoints_and_villages_peak_one_level_higher()
    {
        CrowdProfileBuilder.Build(60, Vector(("leisure.beaches", 0.9)), false).Peak.ShouldBe(4);
        CrowdProfileBuilder.Build(60, Vector(("nature.cliffs_gorges", 0.8)), false).Peak.ShouldBe(4);
        CrowdProfileBuilder.Build(60, Vector(("villages.perched", 0.7)), false).Peak.ShouldBe(4);
        CrowdProfileBuilder.Build(99, Vector(("nature.viewpoints", 0.9)), false).Peak.ShouldBe(5);
        CrowdProfileBuilder.Build(60, Vector(("history.military", 0.9)), false).Peak.ShouldBe(3);
        CrowdProfileBuilder.Build(60, Vector(("nature.viewpoints", 0.3)), false).Peak.ShouldBe(3);
    }

    [Fact]
    public void Weekend_and_midday_deltas_follow_the_rules()
    {
        CrowdProfileBuilder.Build(50, Vector(("leisure.beaches", 0.9)), false).ShouldBe(new CrowdProfile(2, 3, 4, 1, 1));
        CrowdProfileBuilder.Build(49, Vector(("nature.cliffs_gorges", 0.9)), false).ShouldBe(new CrowdProfile(1, 2, 3, 0, 1));
        CrowdProfileBuilder.Build(80, Vector(("history.military", 0.9)), false).ShouldBe(new CrowdProfile(3, 4, 4, 1, 0));
    }

    [Fact]
    public void The_editorial_saturated_list_overrides_the_computed_profile()
    {
        CrowdProfileBuilder.Build(5, Vector(("history.military", 0.9)), true).ShouldBe(new CrowdProfile(4, 5, 5, 1, 0));
    }

    [Theory]
    [InlineData(45, 40, 2, true)]
    [InlineData(44, 40, 2, false)]
    [InlineData(70, 41, 2, false)]
    [InlineData(70, 40, 3, false)]
    [InlineData(100, 0, 1, true)]
    public void A_hidden_gem_is_important_unread_and_quiet(int importance, int percentile, int peak, bool expected) =>
        HiddenGemRule.IsHiddenGem(importance, percentile, new CrowdProfile(1, 1, peak, 0, 0)).ShouldBe(expected);
}

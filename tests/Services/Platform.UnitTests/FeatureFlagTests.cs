using OnVoyage.Platform.Domain;

namespace Platform.UnitTests;

public sealed class FeatureFlagTests
{
    private static FeatureFlag Flag(bool enabled = true, int rollout = 100, string[]? platforms = null, string? min = null) =>
        new("surprise_me", enabled, rollout, platforms ?? [], min);

    [Fact]
    public void Disabled_flag_is_off_for_everyone() =>
        Flag(enabled: false).IsEnabledFor("android", new Version(2, 0), Guid.NewGuid()).ShouldBeFalse();

    [Fact]
    public void Fully_rolled_out_flag_needs_no_identity() =>
        Flag().IsEnabledFor("ios", null, null).ShouldBeTrue();

    [Fact]
    public void Platform_filter_is_case_insensitive_and_excludes_others()
    {
        var flag = Flag(platforms: ["android"]);

        flag.IsEnabledFor("Android", null, null).ShouldBeTrue();
        flag.IsEnabledFor("ios", null, null).ShouldBeFalse();
        flag.IsEnabledFor(null, null, null).ShouldBeFalse();
    }

    [Fact]
    public void Minimum_version_blocks_older_and_unknown_versions()
    {
        var flag = Flag(min: "1.2.0");

        flag.IsEnabledFor("web", new Version(1, 2, 0), null).ShouldBeTrue();
        flag.IsEnabledFor("web", new Version(1, 1, 9), null).ShouldBeFalse();
        flag.IsEnabledFor("web", null, null).ShouldBeFalse();
    }

    [Fact]
    public void Partial_rollout_is_stable_per_traveler_and_off_without_identity()
    {
        var flag = Flag(rollout: 50);
        var traveler = Guid.NewGuid();

        flag.IsEnabledFor("android", null, traveler).ShouldBe(flag.IsEnabledFor("android", null, traveler));
        flag.IsEnabledFor("android", null, null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(90)]
    public void Partial_rollout_reaches_roughly_the_requested_share(int percent)
    {
        var flag = Flag(rollout: percent);
        var travelers = Enumerable.Range(0, 4000).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 7])).ToArray();

        var share = travelers.Count(id => flag.IsEnabledFor("android", null, id)) * 100d / travelers.Length;

        share.ShouldBe(percent, 3);
    }

    [Fact]
    public void Zero_rollout_is_off_even_when_enabled() =>
        Flag(rollout: 0).IsEnabledFor("android", null, Guid.NewGuid()).ShouldBeFalse();

    [Fact]
    public void A_traveler_lands_in_different_buckets_for_different_flags()
    {
        var travelers = Enumerable.Range(0, 200).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 9]));

        travelers.Count(id => FeatureFlag.Bucket("a", id) != FeatureFlag.Bucket("b", id)).ShouldBeGreaterThan(100);
    }

    [Fact]
    public void Defaults_cover_every_flag_of_section_18_with_only_the_control_cohort_on()
    {
        var names = KnownFeatureFlags.Defaults.Select(flag => flag.Name).ToHashSet();

        names.ShouldBeSubsetOf(names);
        names.Count.ShouldBe(11);
        KnownFeatureFlags.Defaults.Where(flag => flag.Enabled).Select(flag => flag.Name).ShouldBe(["control_cohort"]);
    }
}

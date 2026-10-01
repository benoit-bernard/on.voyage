using Microsoft.Extensions.Time.Testing;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Contracts;

namespace Discovery.UnitTests;

public sealed class ValidationTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private static readonly IngestInteractionsValidator Validator = new(Clock);

    private static InteractionDto Like(Guid? poi = null) => new(Guid.NewGuid(), "like", poi ?? Guid.NewGuid(), Clock.GetUtcNow().AddMinutes(-5));

    private static string Check(params InteractionDto[] items)
    {
        var result = Validator.Validate(new IngestInteractionsCommand(Guid.NewGuid(), items));
        return result.IsValid ? string.Empty : result.Errors[0].ErrorMessage;
    }

    [Fact]
    public void A_correct_batch_passes() => Check(Like()).ShouldBeEmpty();

    [Fact]
    public void An_empty_or_oversized_batch_is_refused()
    {
        Check().ShouldNotBeEmpty();
        Check([.. Enumerable.Range(0, 201).Select(_ => Like())]).ShouldNotBeEmpty();
        Check([.. Enumerable.Range(0, 200).Select(_ => Like())]).ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_kinds_and_missing_places_are_refused()
    {
        Check(Like() with { Kind = "love" }).ShouldContain("unknown");
        Check(Like() with { PoiId = null }).ShouldContain("poiId");
    }

    [Fact]
    public void Category_signals_need_a_level_one_code()
    {
        var onboarding = new InteractionDto(Guid.NewGuid(), "onboarding_category", null, Clock.GetUtcNow(), CategoryCode: "nature", Value: 1);
        Check(onboarding).ShouldBeEmpty();
        Check(onboarding with { CategoryCode = "nature.coast" }).ShouldContain("level-1");
        Check(onboarding with { CategoryCode = null }).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_visit_needs_a_confidence_and_a_stay_and_the_future_is_refused()
    {
        var visit = Like() with { Kind = "visit", Confidence = 0.8, DwellS = 400 };
        Check(visit).ShouldBeEmpty();
        Check(visit with { Confidence = 1.4 }).ShouldNotBeEmpty();
        Check(visit with { DwellS = null }).ShouldNotBeEmpty();
        Check(Like() with { OccurredAt = Clock.GetUtcNow().AddHours(2) }).ShouldContain("future");
    }
}

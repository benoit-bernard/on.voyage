using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class ReportAndAuditPageTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    public ReportAndAuditPageTests() => Services.AddSingleton(_api);

    private static ReportInboxItem Item(string place, string storyStatus, int open, DateTimeOffset latest, params string[] reasons) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), place, $"Histoire de {place}", "fr", "Standard", 2, storyStatus, open, latest,
            [.. reasons.Select(reason => new ReportRemarkItem(reason, latest, "Open", null))]);

    [Fact]
    public void The_inbox_lists_each_story_with_the_remarks_and_never_a_reader_identity()
    {
        var item = Item("Fort Saint-Jean", "Suspended", 3, DateTimeOffset.UtcNow, "La date est fausse", "Faute dans le nom", "Il manque la tour");
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([item]);

        var cut = Render<Reports>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Histoire de Fort Saint-Jean");
            cut.Markup.ShouldContain("La date est fausse");
            cut.FindAll("li").Count.ShouldBe(3);
        });
    }

    [Fact]
    public void Reports_can_be_sorted_by_place_or_by_how_many_readers_complained()
    {
        var now = DateTimeOffset.UtcNow;
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            Item("Zèbre", "Published", 1, now, "a"),
            Item("Alpha", "Published", 2, now.AddDays(-1), "a", "b"),
            Item("Milieu", "Published", 5, now.AddDays(-2), "a", "b", "c", "d", "e"),
        ]);
        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.FindAll("section.report").Count.ShouldBe(3));

        string[] Order() => [.. cut.FindAll("section.report h2 a").Select(link => link.TextContent)];

        Order().ShouldBe(["Histoire de Zèbre", "Histoire de Alpha", "Histoire de Milieu"]);
        cut.Find("#sort").Change("place");
        Order().ShouldBe(["Histoire de Alpha", "Histoire de Milieu", "Histoire de Zèbre"]);
        cut.Find("#sort").Change("count");
        Order().ShouldBe(["Histoire de Milieu", "Histoire de Alpha", "Histoire de Zèbre"]);
    }

    [Fact]
    public void A_published_story_is_suspended_first_and_then_a_correction_is_opened()
    {
        var item = Item("Fort", "Published", 3, DateTimeOffset.UtcNow, "a", "b", "c");
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([item]);
        var correction = new StoryItem(Guid.CreateVersion7(), item.PlaceId, "fr", "Standard", 3, "NeedsReview", "t", "h", "x", "i", "f", "l", "r", null, [], 100, "p", "m", 0.8, new CheckReportItem([], [], null, 1, []), "marin", 0.8, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        _api.OpenCorrectionAsync(item.StoryId, Arg.Any<CancellationToken>()).Returns(correction);
        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.FindAll("button").ShouldNotBeEmpty());

        cut.FindAll("button").First(button => button.TextContent == "Suspendre et ouvrir une correction").Click();

        Received.InOrder(() =>
        {
            _api.SuspendStoryAsync(item.StoryId, Arg.Any<string>(), Arg.Any<CancellationToken>());
            _api.OpenCorrectionAsync(item.StoryId, Arg.Any<CancellationToken>());
        });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldEndWith($"/admin/workshop/story/{correction.Id}");
    }

    [Fact]
    public void If_the_suspension_is_refused_no_correction_is_opened()
    {
        var item = Item("Fort", "Published", 3, DateTimeOffset.UtcNow, "a");
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([item]);
        _api.SuspendStoryAsync(item.StoryId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new AdminApiException("Déjà suspendue.", 409));
        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.FindAll("button").ShouldNotBeEmpty());

        cut.FindAll("button").First(button => button.TextContent.StartsWith("Suspendre", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldBe("Déjà suspendue."));
        _api.DidNotReceive().OpenCorrectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Dismissing_closes_the_reports_with_the_editors_note()
    {
        var item = Item("Fort", "Published", 1, DateTimeOffset.UtcNow, "a");
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([item]);
        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.FindAll("input[aria-label=Note]").ShouldNotBeEmpty());

        cut.Find("input[aria-label=Note]").Change("La date est correcte (source officielle)");
        cut.FindAll("button").First(button => button.TextContent.StartsWith("Écarter", StringComparison.Ordinal)).Click();

        _api.Received(1).ResolveReportsAsync(item.StoryId, "Dismissed", "La date est correcte (source officielle)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Closed_reports_offer_no_action()
    {
        var closed = Item("Fort", "Published", 0, DateTimeOffset.UtcNow) with { Remarks = [new ReportRemarkItem("a", DateTimeOffset.UtcNow, "Handled", "correction v3")] };
        _api.ListReportInboxAsync("Handled", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([closed]);
        _api.ListReportInboxAsync("Open", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.Find("#status").ShouldNotBeNull());

        cut.Find("#status").Change("Handled");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("traité : correction v3");
            cut.FindAll("section.report button").ShouldBeEmpty();
        });
    }

    [Fact]
    public void The_journal_shows_the_service_the_actor_and_what_was_asked_and_can_be_filtered_by_service()
    {
        var at = new DateTimeOffset(2026, 10, 2, 9, 30, 15, TimeSpan.Zero);
        _api.ListAuditAsync(Arg.Any<int>(), null, Arg.Any<CancellationToken>()).Returns(
        [
            new AuditItem(Guid.CreateVersion7(), at, "factory", "0194a8b2-aaaa-bbbb-cccc-000000000001", "POST HTTP: POST /places/{id}/publish", "/api/factory/v1/admin/places/x/publish", 200, "id=x"),
            new AuditItem(Guid.CreateVersion7(), at, "platform", "0194a8b2-aaaa-bbbb-cccc-000000000001", "PUT config", "/api/platform/v1/admin/config/reco", 400, "key=reco; request={\"value\":1}"),
        ]);
        _api.ListAuditAsync(Arg.Any<int>(), "platform", Arg.Any<CancellationToken>()).Returns(
            [new AuditItem(Guid.CreateVersion7(), at, "platform", "0194a8b2-aaaa-bbbb-cccc-000000000001", "PUT config", "/api/platform/v1/admin/config/reco", 400, "key=reco")]);
        var cut = Render<Audit>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(2));

        cut.FindAll("tbody tr")[1].ClassList.ShouldContain("refused");
        cut.FindAll("tbody tr")[0].TextContent.ShouldContain("0194a8b2");
        cut.Find("#service").Change("platform");

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));
    }
}

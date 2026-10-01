using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class BatchPageTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    public BatchPageTests()
    {
        Services.AddSingleton(_api);
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([new DestinationItem("marseille", "Marseille", 43.3, 5.4)]);
    }

    private static BatchProgressItem Progress(int succeeded, int failed, int toReview, int pending, int total = 100) =>
        new(new BatchHeaderItem(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "admin", new BatchCriteriaItem("marseille", null, ["Candidate"], "fr", "Standard", total), total), pending, 0, succeeded, toReview, failed, pending == 0);

    [Fact]
    public void The_list_shows_each_batch_with_done_failed_and_to_review_counts()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Progress(87, 8, 5, 0)]);

        var cut = Render<Batches>();

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find(".summary").TextContent;
            summary.ShouldContain("87");
            summary.ShouldContain("terminé(s) sur 100");
            summary.ShouldContain("8 échec(s)");
            summary.ShouldContain("5 à relire");
        });
    }

    [Fact]
    public void Starting_a_batch_sends_the_chosen_criteria()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.CreateBatchAsync(Arg.Any<NewBatch>(), Arg.Any<CancellationToken>()).Returns(Guid.CreateVersion7());
        var cut = Render<Batches>();
        cut.WaitForAssertion(() => cut.FindAll("#limit").ShouldNotBeEmpty());

        cut.Find("#minimum").Change("50");
        cut.Find("#limit").Change("30");
        cut.Find("#lang").Change("en");
        cut.FindAll("input[type=checkbox]")[^1].Change(false); // published places off
        cut.FindAll("button").First(button => button.TextContent == "Lancer le lot").Click();

        _api.Received(1).CreateBatchAsync(
            Arg.Is<NewBatch>(batch => batch.Destination == "marseille" && batch.MinImportance == 50 && batch.Limit == 30 && batch.Lang == "en" && batch.PlaceStatuses.SequenceEqual(new[] { "Candidate" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_batch_the_api_refuses_shows_the_reason()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.CreateBatchAsync(Arg.Any<NewBatch>(), Arg.Any<CancellationToken>()).Returns<Task<Guid>>(_ => throw new AdminApiException("No place matches.", 409));
        var cut = Render<Batches>();
        cut.WaitForAssertion(() => cut.FindAll("button").ShouldNotBeEmpty());

        cut.FindAll("button").First(button => button.TextContent == "Lancer le lot").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldBe("No place matches."));
    }

    [Fact]
    public void The_detail_lists_failures_first_and_retrying_requeues_them()
    {
        var progress = Progress(8, 2, 1, 0, 10);
        var id = progress.Batch.Id;
        JobItem Job(string name, string state, string? error = null, string? outcome = null) => new(Guid.CreateVersion7(), Guid.CreateVersion7(), name, state, state == "Failed" ? "facts" : "done", state == "Failed" ? 3 : 1, error, state == "Succeeded" ? Guid.CreateVersion7() : null, outcome, DateTimeOffset.UtcNow);
        _api.GetBatchAsync(id, Arg.Any<CancellationToken>()).Returns(new BatchDetailItem(progress, [Job("Alpha", "Succeeded", outcome: "Checked"), Job("Zèbre", "Failed", "provider down"), Job("Beta", "Succeeded", outcome: "NeedsReview")]));
        _api.RetryBatchAsync(id, Arg.Any<CancellationToken>()).Returns(2);

        var cut = Render<BatchDetail>(parameters => parameters.Add(p => p.Id, id));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            rows[0].TextContent.ShouldContain("Zèbre");
            rows[0].TextContent.ShouldContain("provider down");
            rows[1].TextContent.ShouldContain("à relire");
            rows[2].TextContent.ShouldContain("Alpha");
        });

        cut.FindAll("button").First(button => button.TextContent.StartsWith("Relancer", StringComparison.Ordinal)).Click();

        _api.Received(1).RetryBatchAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_finished_batch_without_failures_offers_no_retry()
    {
        var progress = Progress(10, 0, 0, 0, 10);
        _api.GetBatchAsync(progress.Batch.Id, Arg.Any<CancellationToken>()).Returns(new BatchDetailItem(progress, []));

        var cut = Render<BatchDetail>(parameters => parameters.Add(p => p.Id, progress.Batch.Id));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("10"));
        cut.FindAll("button").ShouldBeEmpty();
    }
}

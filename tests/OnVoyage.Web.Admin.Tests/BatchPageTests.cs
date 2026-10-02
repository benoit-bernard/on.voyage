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

    private static BatchProgressItem Detailed(int succeeded, int failed, int cancelled, int pending, double cost, double? budget, int total = 10) =>
        new(new BatchHeaderItem(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "admin", new BatchCriteriaItem("marseille", null, ["Candidate"], "fr", "Standard", total, budget), total), pending, 0, succeeded, 0, failed, pending == 0, cancelled, cost, "");

    [Fact]
    public void The_list_shows_the_cost_against_the_budget_and_flags_a_batch_over_its_cap()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Detailed(4, 0, 6, 0, 2.5, 2.0)]);

        var cut = Render<Batches>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".cost").TextContent.ShouldBe("2.5000 / 2.00 USD");
            cut.Find(".cost").ClassList.ShouldContain("bad");
            cut.Find(".summary").TextContent.ShouldContain("6 annulé(s)");
            cut.Find("tbody").TextContent.ShouldContain("Arrêté");
        });
    }

    [Fact]
    public void The_list_can_be_filtered_by_state()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Detailed(10, 0, 0, 0, 1, null), Detailed(8, 2, 0, 0, 1, null), Detailed(3, 0, 0, 7, 1, null)]);
        var cut = Render<Batches>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));

        cut.Find("#filter-status").Change("failures");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));

        cut.Find("#filter-status").Change("running");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.ShouldBe(1);
            cut.Find("tbody").TextContent.ShouldContain("En cours");
        });

        cut.Find("#filter-status").Change("");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
    }

    [Fact]
    public void A_budget_typed_in_the_form_is_sent_with_the_batch()
    {
        _api.ListBatchesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.CreateBatchAsync(Arg.Any<NewBatch>(), Arg.Any<CancellationToken>()).Returns(Guid.CreateVersion7());
        var cut = Render<Batches>();
        cut.WaitForAssertion(() => cut.FindAll("#budget").ShouldNotBeEmpty());

        cut.Find("#budget").Change("3.5");
        cut.FindAll("button").First(button => button.TextContent == "Lancer le lot").Click();

        _api.Received(1).CreateBatchAsync(Arg.Is<NewBatch>(batch => batch.BudgetUsd == 3.5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_detail_can_cancel_the_waiting_jobs_retry_one_job_and_filter_by_state()
    {
        var progress = Detailed(4, 1, 1, 4, 1, null);
        var id = progress.Batch.Id;
        JobItem Job(string name, string state, string? error = null) => new(Guid.CreateVersion7(), Guid.CreateVersion7(), name, state, "queued", 1, error, null, null, DateTimeOffset.UtcNow);
        var failed = Job("Zèbre", "Failed", "provider down");
        _api.GetBatchAsync(id, Arg.Any<CancellationToken>()).Returns(new BatchDetailItem(progress, [Job("Alpha", "Pending"), failed, Job("Beta", "Cancelled", "budget_exhausted")]));
        _api.CancelBatchAsync(id, Arg.Any<CancellationToken>()).Returns(4);

        var cut = Render<BatchDetail>(parameters => parameters.Add(p => p.Id, id));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
        cut.Markup.ShouldContain("Plafond de coût atteint");

        cut.Find("#job-filter").Change("Failed");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.ShouldBe(1);
            cut.Find("tbody").TextContent.ShouldContain("Zèbre");
        });
        cut.Find("tbody button").Click();
        _api.Received(1).RetryJobAsync(failed.Id, Arg.Any<CancellationToken>());

        cut.FindAll("button").First(button => button.TextContent.StartsWith("Annuler", StringComparison.Ordinal)).Click();
        _api.Received(1).CancelBatchAsync(id, Arg.Any<CancellationToken>());
    }
}

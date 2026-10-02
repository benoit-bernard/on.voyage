using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class BootstrapPageTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    public BootstrapPageTests()
    {
        Services.AddSingleton(_api);
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([new DestinationItem("marseille", "Marseille", 43.3, 5.4)]);
    }

    private static BootstrapRunItem Run(string status, int done, int total, string? outcome = null, string? error = null, double cost = 1.5, bool cancelRequested = false, params BootstrapStepItem[] steps) =>
        new(Guid.CreateVersion7(), "marseille", status, "admin", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, total, null, "fr", false, 5, cost, total, done, done, 2, 0, 1, outcome, error, cancelRequested, steps, status is "Completed" or "Stopped" or "Failed");

    [Fact]
    public void The_form_launches_a_bootstrap_with_the_chosen_cap_and_options()
    {
        _api.ListBootstrapRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.StartBootstrapAsync(Arg.Any<NewBootstrap>(), Arg.Any<CancellationToken>()).Returns(Guid.CreateVersion7());
        var cut = Render<Bootstrap>();
        cut.WaitForAssertion(() => cut.FindAll("#max-places").ShouldNotBeEmpty());

        cut.Find("#max-places").Change("12");
        cut.Find("#min-importance").Change("40");
        cut.Find("#budget").Change("2.5");
        cut.Find("#lang").Change("en");
        cut.Find("#auto-publish").Change(true);
        cut.FindAll("button").First(button => button.TextContent == "Lancer l'amorçage").Click();

        _api.Received(1).StartBootstrapAsync(
            Arg.Is<NewBootstrap>(run => run.Destination == "marseille" && run.MaxPlaces == 12 && run.MinImportance == 40 && run.BudgetUsd == 2.5 && run.Lang == "en" && run.AutoPublish && !run.ForceImport && !run.SkipImport),
            Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("Amorçage demandé"));
    }

    [Fact]
    public void A_refused_bootstrap_shows_the_reason()
    {
        _api.ListBootstrapRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.StartBootstrapAsync(Arg.Any<NewBootstrap>(), Arg.Any<CancellationToken>()).Returns<Task<Guid>>(_ => throw new AdminApiException("Aucun tarif n'est configuré.", 409));
        var cut = Render<Bootstrap>();
        cut.WaitForAssertion(() => cut.FindAll("button").ShouldNotBeEmpty());

        cut.FindAll("button").First(button => button.TextContent == "Lancer l'amorçage").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldBe("Aucun tarif n'est configuré."));
    }

    [Fact]
    public void A_running_bootstrap_shows_its_progress_and_cost_against_the_cap_and_can_be_stopped()
    {
        var running = Run("Running", 12, 40, cost: 1.5);
        _api.ListBootstrapRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([running]);
        var cut = Render<Bootstrap>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("tbody tr");
            row.TextContent.ShouldContain("En cours");
            row.TextContent.ShouldContain("12 / 40 lieux");
            row.TextContent.ShouldContain("1.5000 / 5.00 USD");
            row.QuerySelector("progress")!.GetAttribute("value").ShouldBe("12");
        });

        cut.FindAll("button").First(button => button.TextContent == "Arrêter").Click();

        _api.Received(1).CancelBootstrapRunAsync(running.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_finished_run_says_why_it_stopped_and_lists_its_steps()
    {
        var stopped = Run("Stopped", 7, 40, "budget_exhausted", steps: [new BootstrapStepItem("selection", "done", "40 places"), new BootstrapStepItem("place:fort", "skipped", "pas assez de faits")]);
        _api.ListBootstrapRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([stopped]);
        var cut = Render<Bootstrap>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Plafond de coût atteint"));

        cut.FindAll("button").ShouldNotContain(button => button.TextContent == "Arrêter");
        cut.FindAll("button").First(button => button.TextContent == "Détail").Click();

        var steps = cut.Find("ul.steps").TextContent;
        steps.ShouldContain("selection : done — 40 places");
        steps.ShouldContain("pas assez de faits");
        cut.Find("li.bad").TextContent.ShouldContain("place:fort");
    }

    [Fact]
    public void A_failed_run_shows_the_error()
    {
        _api.ListBootstrapRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Run("Failed", 0, 0, error: "osm2pgsql a échoué")]);
        var cut = Render<Bootstrap>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").ClassList.ShouldContain("refused");
            cut.Find("span.bad").TextContent.ShouldBe("osm2pgsql a échoué");
        });
    }

    [Fact]
    public void The_dead_letter_view_links_each_message_to_its_batch_and_shows_the_error()
    {
        var batch = Guid.CreateVersion7();
        _api.ListDeadLettersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new DeadLetterItem(Guid.NewGuid(), "RunBatchJobCommand", DateTimeOffset.UtcNow, "BatchJobExhaustedException", "Job x failed 3 times.", Guid.NewGuid(), batch, "Fort Saint-Jean"),
            new DeadLetterItem(Guid.NewGuid(), "ImportPlacesCommand", DateTimeOffset.UtcNow, null, "boom", null, null, null),
        ]);

        var cut = Render<DeadLetters>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            rows.Count.ShouldBe(2);
            rows[0].QuerySelector("a")!.GetAttribute("href").ShouldBe($"/admin/batches/{batch}");
            rows[0].TextContent.ShouldContain("Fort Saint-Jean");
            rows[0].TextContent.ShouldContain("failed 3 times");
            rows[1].TextContent.ShouldContain("ImportPlacesCommand");
        });
    }

    [Fact]
    public void Without_dead_letters_the_view_says_so()
    {
        _api.ListDeadLettersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var cut = Render<DeadLetters>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Aucune lettre morte"));
    }
}

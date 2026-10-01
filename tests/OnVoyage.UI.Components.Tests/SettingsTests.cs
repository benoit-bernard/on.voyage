using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Privacy;
using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class SettingsTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly IPrivacyApi _api = Substitute.For<IPrivacyApi>();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly Guid _fort = Guid.NewGuid();

    public SettingsTests()
    {
        _api.GetConsentsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConsentDto>>([]));
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(new Dictionary<string, double> { ["history"] = 0.7, ["nature"] = -0.3 }, new Dictionary<string, DateTimeOffset>()));
        _api.GetHistoryAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<HistoryItemDto>>([new HistoryItemDto(_fort, "fort", "Fort Saint-Jean", Now.AddDays(-2), true, false)]));

        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton<OnVoyage.App.Core.Analytics.AnalyticsConsent>();
        Services.AddSingleton(_api);
        Services.AddSingleton<IProfileStore>(_profiles);
        var auth = Substitute.For<IAuthClient>();
        auth.StartAnonymousAsync(Arg.Any<CancellationToken>()).Returns(AuthResult.Success(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, [])));
        Services.AddSingleton(new SessionService(auth, new InMemorySessionStore(), TimeProvider.System));
    }

    private static Task<ProfileDto> Profile(Dictionary<string, double> vector, Dictionary<string, DateTimeOffset> locks) =>
        Task.FromResult(new ProfileDto(vector, 12, "interesting", 1, locks, [], "personalized"));

    [Fact]
    public void The_profile_shows_each_category_with_a_slider_and_marks_locked_ones()
    {
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(new Dictionary<string, double> { ["history"] = 0.7 }, new Dictionary<string, DateTimeOffset> { ["nature"] = Now.AddDays(10) }));
        var cut = Render<Settings>();

        cut.WaitForAssertion(() => cut.FindAll("input[type=range]").Count.ShouldBe(10));
        cut.Find("#axis-history").GetAttribute("value").ShouldBe("0.7");
        cut.Markup.ShouldContain("🔒");
        cut.Markup.ShouldContain("profondeur 12");
    }

    [Fact]
    public async Task Moving_a_slider_corrects_that_category_and_updates_the_local_profile()
    {
        _api.CorrectProfileAsync(Arg.Any<IReadOnlyList<ProfileCorrection>>(), Arg.Any<CancellationToken>())
            .Returns(Profile(new Dictionary<string, double> { ["history"] = 0.7, ["nature"] = 0d }, new Dictionary<string, DateTimeOffset> { ["nature"] = Now.AddDays(30) }));
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("#axis-nature"));

        cut.Find("button[aria-label='Remettre la nature à zéro']").Click();

        await _api.Received(1).CorrectProfileAsync(Arg.Is<IReadOnlyList<ProfileCorrection>>(c => c.Single().Code == "nature" && c.Single().Value == 0d), Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("🔒"));
        (await _profiles.LoadAsync(Ct)).Affinities["nature"].ShouldBe(0d);
    }

    [Fact]
    public async Task The_ethical_mode_is_kept_on_the_device_and_sent_to_the_server()
    {
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => cut.FindAll("input[name=ethical]").Count.ShouldBe(3));

        cut.FindAll("input[name=ethical]")[2].Change(true);

        await _api.Received(1).UpdateSettingsAsync(Arg.Is<SettingsPatch>(p => p.EthicalMode == "strong"), Arg.Any<CancellationToken>());
        (await _profiles.LoadAsync(Ct)).EthicalMode.ShouldBe("strong");
    }

    [Fact]
    public async Task The_statistics_consent_starts_refused_and_each_choice_is_recorded_with_the_text_version()
    {
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => cut.FindAll("button[aria-pressed]").Count.ShouldBe(2));
        cut.FindAll("button[aria-pressed]")[1].GetAttribute("aria-pressed").ShouldBe("true"); // "Refuser" is the pressed one

        cut.FindAll("button[aria-pressed]")[0].Click();

        await _api.Received(1).SetConsentAsync("analytics", true, ConsentTexts.AnalyticsVersion, Arg.Any<CancellationToken>());
        cut.FindAll("button[aria-pressed]")[0].GetAttribute("aria-pressed").ShouldBe("true");
    }

    [Fact]
    public async Task Deleting_a_history_entry_recomputes_and_reloads()
    {
        _api.DeleteHistoryAsync(_fort, Arg.Any<CancellationToken>()).Returns(new InteractionBatchResponse(new Dictionary<string, double> { ["nature"] = 0.2 }, 9, 1, 0, 0, []));
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => cut.Find(".history li").TextContent.ShouldContain("Fort Saint-Jean"));

        _api.GetHistoryAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<HistoryItemDto>>([]));
        cut.Find(".history button").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Aucun lieu écouté"));
        var local = await _profiles.LoadAsync(Ct);
        local.Affinities.ShouldBe(new Dictionary<string, double> { ["nature"] = 0.2 });
        local.Depth.ShouldBe(9);
    }

    [Fact]
    public void An_export_is_requested_then_offered_for_download_as_json()
    {
        var id = Guid.NewGuid();
        _api.StartExportAsync(Arg.Any<CancellationToken>()).Returns(new ExportStatusDto(id, "pending", Now, null, ["discovery"]));
        _api.GetExportAsync(id, Arg.Any<CancellationToken>()).Returns(new ExportStatusDto(id, "ready", Now, Now.AddHours(24), []));
        _api.DownloadExportAsync(id, Arg.Any<CancellationToken>()).Returns("{\"interactions\":[]}");
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("#export"));

        cut.FindAll("button").First(b => b.TextContent == "Préparer mon export").Click();

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a[download]");
            link.GetAttribute("download").ShouldBe("on-voyage-mes-donnees.json");
            link.GetAttribute("href")!.ShouldStartWith("data:application/json;charset=utf-8,");
        });
    }

    [Fact]
    public async Task Deleting_the_account_needs_a_confirmation_and_then_clears_the_device()
    {
        await _profiles.SaveAsync(new LocalProfile { Saved = [_fort], Affinities = new() { ["history"] = 0.9 } }, Ct);
        _api.RequestDeletionAsync(Arg.Any<CancellationToken>()).Returns(new DeletionStatusDto("pending", Now, null, ["discovery"]));
        var cut = Render<Settings>();
        var button = cut.WaitForElement("button.danger");
        button.HasAttribute("disabled").ShouldBeTrue();

        cut.Find(".check input").Change(true);
        cut.Find("button.danger").Click();

        await _api.Received(1).RequestDeletionAsync(Arg.Any<CancellationToken>());
        var local = await _profiles.LoadAsync(Ct);
        local.Saved.ShouldBeEmpty();
        local.Affinities.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_deletion_request_changes_nothing_on_the_device()
    {
        await _profiles.SaveAsync(new LocalProfile { Saved = [_fort] }, Ct);
        _api.RequestDeletionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<DeletionStatusDto>(new HttpRequestException("offline")));
        var cut = Render<Settings>();
        cut.WaitForElement("button.danger");
        cut.Find(".check input").Change(true);
        cut.Find("button.danger").Click();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("Rien n'a été supprimé"));
        (await _profiles.LoadAsync(Ct)).Saved.ShouldContain(_fort);
    }

    [Fact]
    public void Offline_the_sections_that_need_the_server_say_so()
    {
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<ProfileDto>(new HttpRequestException("offline")));
        _api.GetHistoryAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<HistoryItemDto>>(new HttpRequestException("offline")));
        var cut = Render<Settings>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Profil indisponible hors connexion.");
            cut.Markup.ShouldContain("Historique indisponible hors connexion.");
        });
    }
}

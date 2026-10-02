using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;
using OnVoyage.Web.Admin.Text;

namespace OnVoyage.Web.Admin.Tests;

/// <summary>T-1202: the screens of the founding creators and of the simple moderation queue, on fixed data (no service behind them).</summary>
public sealed class CreatorsPageTests : BunitContext
{
    private static readonly Guid CreatorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly ICreatorsAdminApi _api = Substitute.For<ICreatorsAdminApi>();

    public CreatorsPageTests() => Services.AddSingleton(_api);

    private static PublishBlockDto TermsRequired => new("terms_required", "serveur");

    private static AdminCreatorDetailDto Detail(string status = "draft", string? termsVersion = null, PublishBlockDto? block = null, string[]? specialties = null, AdminContentDto[]? contents = null, AdminPlaceLinkDto[]? links = null, AdminTipDto[]? tips = null) =>
        new(CreatorId, null, "marie_marseille", "Marie", "Marseillaise.", null, ["fr"], specialties ?? ["history"], [], [], status, true, termsVersion, termsVersion == "fondateur" ? "H-009/marie.pdf" : null, termsVersion is null ? null : Now, 3, block, contents ?? [], links ?? [], tips ?? []);

    private static AdminContentDto Content(string title = "Marseille en 48 h", string status = "imported", bool commercial = false) =>
        new(Guid.NewGuid(), "youtube", "video", title, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", null, null, 600, Now, commercial, status, []);

    private static AdminCreatorSummaryDto Summary(string handle, string status, string? terms, PublishBlockDto? block, int places = 0) =>
        new(Guid.NewGuid(), handle, handle.ToUpperInvariant(), status, true, terms, 1, places, false, block);

    private IRenderedComponent<CreatorDetail> ShowDetail(AdminCreatorDetailDto creator)
    {
        _api.GetCreatorAsync(CreatorId, Arg.Any<CancellationToken>()).Returns(creator);
        return Render<CreatorDetail>(parameters => parameters.Add(page => page.Id, CreatorId));
    }

    // ---- list

    [Fact]
    public void The_list_says_why_a_creator_cannot_be_published_in_french()
    {
        _api.ListCreatorsAsync(null, null, Arg.Any<CancellationToken>()).Returns(
        [
            Summary("sans_consentement", "draft", null, new PublishBlockDto("terms_required", "x")),
            Summary("sans_specialite", "draft", "fondateur", new PublishBlockDto("specialty_required", "x")),
            Summary("prete", "draft", "fondateur", null),
            Summary("en_ligne", "published", "fondateur", null, 4),
            Summary("suspendue", "suspended", "fondateur", null),
        ]);

        var cut = Render<CreatorList>();

        cut.WaitForAssertion(() => cut.FindAll("tr.creator").Count.ShouldBe(5));
        string Row(string handle) => cut.FindAll("tr.creator").Single(row => row.TextContent.Contains($"@{handle}", StringComparison.Ordinal)).TextContent;
        Row("sans_consentement").ShouldContain("Aucun consentement");
        Row("sans_consentement").ShouldContain("Publication impossible : les CGU créateurs ne sont pas acceptées et aucun consentement fondateur n'est enregistré.");
        Row("sans_specialite").ShouldContain("Consentement fondateur");
        Row("sans_specialite").ShouldContain("choisissez au moins une spécialité");
        Row("prete").ShouldContain("Prêt à publier");
        Row("en_ligne").ShouldContain("En ligne");
        Row("en_ligne").ShouldContain("Publié");
        Row("suspendue").ShouldContain("Suspendu");
    }

    [Fact]
    public void The_list_filters_by_status_and_text()
    {
        _api.ListCreatorsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var cut = Render<CreatorList>();

        cut.Find("#status").Change("published");
        cut.Find("#search").Change(" marie ");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        cut.WaitForAssertion(() => _api.Received().ListCreatorsAsync("published", "marie", Arg.Any<CancellationToken>()));
        cut.Find("#empty, p.muted").TextContent.ShouldNotBeNull();
    }

    [Fact]
    public void A_founding_creator_is_created_as_a_draft_and_the_editor_lands_on_the_sheet()
    {
        _api.ListCreatorsAsync(null, null, Arg.Any<CancellationToken>()).Returns([]);
        _api.CreateFounderAsync(Arg.Any<CreatorProfileRequest>(), Arg.Any<CancellationToken>()).Returns(Detail());
        var cut = Render<CreatorList>();
        cut.Find("#new-submit").HasAttribute("disabled").ShouldBeTrue(); // nothing to create yet

        cut.Find("#new-handle").Change("marie_marseille");
        cut.Find("#new-name").Change("Marie");
        cut.Find("#new-bio").Change("Marseillaise.");
        cut.Find("input[name=specialty][value=history]").Change(true);
        cut.Find("input[name=specialty][value=nature]").Change(true);
        cut.Find("input[name=language][value=en]").Change(true);
        cut.Find("#new-submit").Click();

        cut.WaitForAssertion(() => _api.Received(1).CreateFounderAsync(
            Arg.Is<CreatorProfileRequest>(request => request.Handle == "marie_marseille" && request.DisplayName == "Marie" && request.Bio == "Marseillaise."
                && request.Specialties!.OrderBy(code => code).SequenceEqual(new[] { "history", "nature" }) && request.Languages!.OrderBy(code => code).SequenceEqual(new[] { "en", "fr" })),
            Arg.Any<CancellationToken>()));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"/admin/creators/{CreatorId}");
    }

    [Fact]
    public void A_taken_handle_is_refused_with_the_suggestion_of_the_service()
    {
        _api.ListCreatorsAsync(null, null, Arg.Any<CancellationToken>()).Returns([]);
        _api.CreateFounderAsync(Arg.Any<CreatorProfileRequest>(), Arg.Any<CancellationToken>()).Returns<AdminCreatorDetailDto>(_ => throw new AdminApiException("Le handle @marie est déjà pris. Suggestion : @marie2.", 409));
        var cut = Render<CreatorList>();

        cut.Find("#new-handle").Change("marie");
        cut.Find("#new-name").Change("Marie");
        cut.Find("#new-submit").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("Suggestion : @marie2."));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldNotContain("/admin/creators/");
    }

    // ---- sheet: publication

    [Fact]
    public void Without_terms_nor_founder_consent_the_publish_button_is_disabled_and_the_french_reason_is_shown()
    {
        var cut = ShowDetail(Detail(block: TermsRequired));

        var button = cut.Find("#publish");
        button.HasAttribute("disabled").ShouldBeTrue();
        button.GetAttribute("aria-describedby").ShouldBe("publish-block");
        cut.Find("#publish-block").TextContent.ShouldBe("Publication impossible : les CGU créateurs ne sont pas acceptées et aucun consentement fondateur n'est enregistré.");
        cut.Find("#status").TextContent.ShouldBe("Brouillon");
        cut.Find("#consent-state").TextContent.ShouldBe("Aucun consentement");
    }

    [Fact]
    public void Without_a_specialty_the_publish_button_is_disabled_with_its_own_reason()
    {
        var cut = ShowDetail(Detail(termsVersion: "fondateur", block: new PublishBlockDto("specialty_required", "x"), specialties: []));

        cut.Find("#publish").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#publish-block").TextContent.ShouldBe("Publication impossible : choisissez au moins une spécialité.");
    }

    [Fact]
    public void With_the_founder_consent_and_a_specialty_publishing_is_possible_and_the_page_goes_online()
    {
        _api.PublishAsync(CreatorId, Arg.Any<CancellationToken>()).Returns(Detail("published", "fondateur"));
        var cut = ShowDetail(Detail(termsVersion: "fondateur"));

        cut.Find("#publish").HasAttribute("disabled").ShouldBeFalse();
        cut.FindAll("#publish-block").ShouldBeEmpty();
        cut.Find("#consent-state").TextContent.ShouldBe("Consentement fondateur");
        cut.Find("#consent").TextContent.ShouldContain("H-009/marie.pdf");
        cut.Find("#publish").Click();

        cut.WaitForAssertion(() => cut.Find("#status").TextContent.ShouldBe("Publié"));
        _api.Received(1).PublishAsync(CreatorId, Arg.Any<CancellationToken>());
        cut.FindAll("#publish").ShouldBeEmpty();
        cut.Find("#unpublish").HasAttribute("disabled").ShouldBeTrue(); // a reason is needed
    }

    [Fact]
    public void A_refusal_of_the_service_is_shown_and_the_sheet_stays_as_it_was()
    {
        _api.PublishAsync(CreatorId, Arg.Any<CancellationToken>()).Returns<AdminCreatorDetailDto>(_ => throw new AdminApiException("Les CGU créateurs ne sont pas acceptées et aucun consentement fondateur n'est enregistré.", 422));
        var cut = ShowDetail(Detail(termsVersion: "fondateur")); // the page believes it can; the service has the last word

        cut.Find("#publish").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("ne sont pas acceptées"));
        cut.Find("#status").TextContent.ShouldBe("Brouillon");
    }

    [Fact]
    public void Recording_the_consent_needs_the_document_reference_and_then_unblocks_the_publication()
    {
        _api.RecordConsentAsync(CreatorId, "H-009/marie-2026-10-01.pdf", null, Arg.Any<CancellationToken>()).Returns(Detail(termsVersion: "fondateur"));
        var cut = ShowDetail(Detail(block: TermsRequired));
        cut.Find("#record-consent").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#publish").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#document-ref").Input("  ");
        cut.Find("#record-consent").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#document-ref").Input("H-009/marie-2026-10-01.pdf");
        cut.Find("#record-consent").HasAttribute("disabled").ShouldBeFalse();
        cut.Find("#record-consent").Click();

        cut.WaitForAssertion(() => cut.Find("#publish").HasAttribute("disabled").ShouldBeFalse());
        _api.Received(1).RecordConsentAsync(CreatorId, "H-009/marie-2026-10-01.pdf", null, Arg.Any<CancellationToken>());
        cut.Find("#consent-state").TextContent.ShouldBe("Consentement fondateur");
    }

    [Fact]
    public void Unpublishing_and_suspending_need_a_reason_which_is_passed_on()
    {
        _api.UnpublishAsync(CreatorId, "à la demande de Marie", Arg.Any<CancellationToken>()).Returns(Detail(termsVersion: "fondateur"));
        _api.SuspendAsync(CreatorId, "contenu trompeur", Arg.Any<CancellationToken>()).Returns(Detail("suspended", "fondateur"));
        var cut = ShowDetail(Detail("published", "fondateur"));
        cut.Find("#unpublish").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#suspend").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#reason").Input("contenu trompeur");
        cut.Find("#suspend").Click();

        cut.WaitForAssertion(() => cut.Find("#status").TextContent.ShouldBe("Suspendu"));
        _api.Received(1).SuspendAsync(CreatorId, "contenu trompeur", Arg.Any<CancellationToken>());
        cut.Find("#publish").TextContent.ShouldBe("Lever la suspension et publier");
        cut.FindAll("#suspend").ShouldBeEmpty();

        // Back to a published page, then withdrawn at the creator's request.
        _api.PublishAsync(CreatorId, Arg.Any<CancellationToken>()).Returns(Detail("published", "fondateur"));
        cut.Find("#publish").Click();
        cut.WaitForAssertion(() => cut.Find("#status").TextContent.ShouldBe("Publié"));
        cut.Find("#reason").Input("à la demande de Marie");
        cut.Find("#unpublish").Click();
        cut.WaitForAssertion(() => cut.Find("#status").TextContent.ShouldBe("Brouillon"));
        _api.Received(1).UnpublishAsync(CreatorId, "à la demande de Marie", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Saving_the_profile_sends_the_edited_fields_and_keeps_what_the_form_does_not_show()
    {
        var detail = Detail(termsVersion: "fondateur", specialties: ["history.military"]) with { DestinationIds = [Guid.Parse("22222222-2222-2222-2222-222222222222")], Links = [new CreatorLinkDto("instagram", "https://www.instagram.com/marie")] };
        _api.UpdateCreatorAsync(CreatorId, Arg.Any<CreatorProfileRequest>(), Arg.Any<CancellationToken>()).Returns(detail);
        var cut = ShowDetail(detail);

        cut.Find("#profile-name").Change("Marie G.");
        cut.Find("input[name=specialty][value=nature]").Change(true);
        cut.Find("#profile-submit").Click();

        cut.WaitForAssertion(() => _api.Received(1).UpdateCreatorAsync(
            CreatorId,
            Arg.Is<CreatorProfileRequest>(request => request.DisplayName == "Marie G." && request.Handle == "marie_marseille"
                && request.Specialties!.OrderBy(code => code).SequenceEqual(new[] { "history.military", "nature" }) // the finer code is kept
                && request.DestinationIds!.Count == 1 && request.Links!.Single().Kind == "instagram"),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void The_account_is_linked_only_with_a_valid_identifier()
    {
        var account = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _api.LinkAccountAsync(CreatorId, account, Arg.Any<CancellationToken>()).Returns(Detail(termsVersion: "fondateur") with { AccountId = account });
        var cut = ShowDetail(Detail(termsVersion: "fondateur"));

        cut.Find("#account-id").Input("pas un identifiant");
        cut.Find("#link-account").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#account-id").Input(account.ToString());
        cut.Find("#link-account").Click();

        cut.WaitForAssertion(() => cut.Find("#consent").TextContent.ShouldContain(account.ToString()));
    }

    // ---- sheet: contents, associations, tips, claim

    [Fact]
    public void A_content_is_added_by_url_with_its_chapters_and_the_advertising_flag()
    {
        var detail = Detail(termsVersion: "fondateur");
        _api.AddContentAsync(CreatorId, Arg.Any<AddContentRequest>(), Arg.Any<CancellationToken>()).Returns(Content());
        var cut = ShowDetail(detail);
        cut.Find("#add-content").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#content-url").Input("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        cut.Find("#content-title").Input("Marseille en 48 h");
        cut.Find("#content-duration").Change("900");
        cut.Find("#content-commercial").Change(true);
        cut.Find("#content-chapters").Change("02:15 Le Panier\n1:02:03 Les Goudes");
        cut.Find("#add-content").Click();

        cut.WaitForAssertion(() => _api.Received(1).AddContentAsync(
            CreatorId,
            Arg.Is<AddContentRequest>(request => request.Url == "https://www.youtube.com/watch?v=dQw4w9WgXcQ" && request.Title == "Marseille en 48 h" && request.DurationSeconds == 900 && request.IsCommercial
                && request.Chapters!.SequenceEqual(new[] { new ChapterDto(135, "Le Panier"), new ChapterDto(3723, "Les Goudes") })),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void An_unreadable_chapter_is_explained_and_nothing_is_sent()
    {
        var cut = ShowDetail(Detail(termsVersion: "fondateur"));

        cut.Find("#content-url").Input("https://youtu.be/dQw4w9WgXcQ");
        cut.Find("#content-title").Input("Titre");
        cut.Find("#content-chapters").Change("Gordes à deux heures");
        cut.Find("#add-content").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("Chapitre illisible"));
        _api.DidNotReceive().AddContentAsync(Arg.Any<Guid>(), Arg.Any<AddContentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Contents_open_the_original_link_label_the_advertising_and_can_be_removed()
    {
        var content = Content("Reportage sponsorisé", commercial: true);
        var cut = ShowDetail(Detail(termsVersion: "fondateur", contents: [content, Content("Retirée", "removed")]));

        var link = cut.Find("#contents a[href='https://www.youtube.com/watch?v=dQw4w9WgXcQ']");
        link.GetAttribute("target").ShouldBe("_blank");
        link.GetAttribute("rel").ShouldBe("noopener noreferrer");
        cut.Find("#contents").TextContent.ShouldContain("Publicité");
        cut.FindAll("#contents tr.content button").Count.ShouldBe(1); // a removed content has nothing to remove

        _api.RemoveContentAsync(CreatorId, content.Id, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        cut.Find("#contents tr.content button").Click();
        cut.WaitForAssertion(() => _api.Received(1).RemoveContentAsync(CreatorId, content.Id, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_place_is_searched_chosen_and_associated_to_a_content_at_a_chapter_and_a_tip_can_be_added()
    {
        var content = Content();
        var fort = new PoiSearchResultDto(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Fort Saint-Jean", "Marseille", "marseille", true);
        _api.SearchPlacesAsync("fort", Arg.Any<CancellationToken>()).Returns([fort]);
        _api.AddPlaceLinkAsync(CreatorId, Arg.Any<AddPlaceLinkRequest>(), Arg.Any<CancellationToken>()).Returns(new AdminPlaceLinkDto(Guid.NewGuid(), fort.PoiId, fort.Name, content.Id, content.Title, 135, 1, "validated", Now));
        _api.SetTipAsync(CreatorId, fort.PoiId, "Venez au coucher du soleil.", Arg.Any<CancellationToken>()).Returns(new AdminTipDto(Guid.NewGuid(), fort.PoiId, fort.Name, "Venez au coucher du soleil.", "published", Now));
        var cut = ShowDetail(Detail(termsVersion: "fondateur", contents: [content]));
        cut.Find("#search-places").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#place-query").Input("fort");
        cut.Find("#search-places").Click();
        cut.WaitForAssertion(() => cut.Find("#place-results").TextContent.ShouldContain("Fort Saint-Jean"));
        cut.Find("#place-results button").Click();
        cut.Find("#chosen h3").TextContent.ShouldBe("Fort Saint-Jean");
        cut.Find("#link-content").Change(content.Id.ToString());
        cut.Find("#link-start").Change("135");
        cut.Find("#add-link").Click();
        cut.WaitForAssertion(() => _api.Received(1).AddPlaceLinkAsync(CreatorId, new AddPlaceLinkRequest(fort.PoiId, content.Id, 135, null), Arg.Any<CancellationToken>()));

        cut.Find("#tip").Input("Venez au coucher du soleil.");
        cut.Find("#save-tip").Click();
        cut.WaitForAssertion(() => _api.Received(1).SetTipAsync(CreatorId, fort.PoiId, "Venez au coucher du soleil.", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_timestamp_without_a_content_is_not_proposed()
    {
        var fort = new PoiSearchResultDto(Guid.NewGuid(), "Fort Saint-Jean", "Marseille", "marseille", true);
        _api.SearchPlacesAsync("fort", Arg.Any<CancellationToken>()).Returns([fort]);
        var cut = ShowDetail(Detail(termsVersion: "fondateur"));
        cut.Find("#place-query").Input("fort");
        cut.Find("#search-places").Click();
        cut.WaitForAssertion(() => cut.Find("#place-results button"));
        cut.Find("#place-results button").Click();

        cut.Find("#link-start").Change("30");

        cut.Find("#add-link").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Associations_show_their_state_and_can_be_validated_withdrawn_or_deleted()
    {
        var proposed = new AdminPlaceLinkDto(Guid.NewGuid(), Guid.NewGuid(), "Calanque de Sormiou", null, null, null, 0.8, "proposed", null);
        var validated = new AdminPlaceLinkDto(Guid.NewGuid(), Guid.NewGuid(), "Fort Saint-Jean", Guid.NewGuid(), "Marseille en 48 h", 135, 1, "validated", Now);
        _api.SetPlaceLinkStatusAsync(CreatorId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(validated);
        var cut = ShowDetail(Detail(termsVersion: "fondateur", links: [proposed, validated]));

        var rows = cut.FindAll("#place-links tr.link");
        rows[0].TextContent.ShouldContain("Proposée");
        rows[0].TextContent.ShouldContain("conseil seul");
        rows[1].TextContent.ShouldContain("Validée");
        rows[1].TextContent.ShouldContain("02:15");

        rows[0].QuerySelectorAll("button").First(button => button.TextContent == "Valider").Click();
        cut.WaitForAssertion(() => _api.Received(1).SetPlaceLinkStatusAsync(CreatorId, proposed.Id, "validated", Arg.Any<CancellationToken>()));
        cut.FindAll("#place-links tr.link")[1].QuerySelectorAll("button").First(button => button.TextContent == "Retirer de la publication").Click();
        cut.WaitForAssertion(() => _api.Received(1).SetPlaceLinkStatusAsync(CreatorId, validated.Id, "rejected", Arg.Any<CancellationToken>()));
        cut.FindAll("#place-links tr.link")[0].QuerySelectorAll("button").First(button => button.TextContent == "Supprimer").Click();
        cut.WaitForAssertion(() => _api.Received(1).RemovePlaceLinkAsync(CreatorId, proposed.Id, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Tips_are_listed_and_a_tip_hidden_by_moderation_says_so()
    {
        var tip = new AdminTipDto(Guid.NewGuid(), Guid.NewGuid(), "Vallon des Auffes", "Allez-y en semaine.", "hidden", Now);
        var cut = ShowDetail(Detail(termsVersion: "fondateur", tips: [tip]));

        cut.Find("#tips").TextContent.ShouldContain("Allez-y en semaine.");
        cut.Find("#tips").TextContent.ShouldContain("masqué par la modération");
        cut.Find("#tips button").Click();
        cut.WaitForAssertion(() => _api.Received(1).RemoveTipAsync(CreatorId, tip.PoiId, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_handle_claim_needs_a_handle_and_a_reason()
    {
        _api.ClaimHandleAsync(CreatorId, "marie", "Titulaire vérifiée par e-mail", Arg.Any<CancellationToken>()).Returns(Detail(termsVersion: "fondateur") with { Handle = "marie" });
        var cut = ShowDetail(Detail(termsVersion: "fondateur"));
        cut.Find("#claim-button").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#claim-handle").Input("marie");
        cut.Find("#claim-button").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#claim-reason").Input("Titulaire vérifiée par e-mail");
        cut.Find("#claim-button").Click();

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldContain("@marie"));
        _api.Received(1).ClaimHandleAsync(CreatorId, "marie", "Titulaire vérifiée par e-mail", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_unknown_creator_shows_the_error_instead_of_a_sheet()
    {
        _api.GetCreatorAsync(CreatorId, Arg.Any<CancellationToken>()).Returns<AdminCreatorDetailDto>(_ => throw new AdminApiException("Créateur introuvable.", 404));

        var cut = Render<CreatorDetail>(parameters => parameters.Add(page => page.Id, CreatorId));

        cut.Find("p.alert.error").TextContent.ShouldContain("Créateur introuvable.");
        cut.FindAll("#publish").ShouldBeEmpty();
    }

    // ---- moderation

    private static ModerationCaseDto Report(string status = "open", string target = "Conseil « Allez-y en semaine. »", string reason = "inaccurate", string? decision = null, string? statement = null) =>
        new(Guid.NewGuid(), "tip", Guid.NewGuid(), target, CreatorId, "marie_marseille", reason, status, decision, statement, Now, status == "open" ? null : Now.AddDays(1));

    [Fact]
    public void The_queue_lists_the_open_reports_with_the_target_the_reason_and_never_the_reporter()
    {
        _api.ListModerationAsync("open", Arg.Any<CancellationToken>()).Returns([Report(reason: "undeclared_ad"), Report(target: "Profil @marie_marseille", reason: "impersonation")]);

        var cut = Render<ModerationQueue>();

        cut.WaitForAssertion(() => cut.FindAll("section.case").Count.ShouldBe(2));
        cut.Markup.ShouldContain("Publicité non déclarée");
        cut.Markup.ShouldContain("Usurpation");
        cut.Markup.ShouldContain("Profil @marie_marseille");
        cut.FindAll("a[href='/admin/creators/11111111-1111-1111-1111-111111111111']").Count.ShouldBe(2);
        cut.Markup.ShouldNotContain("reporter", Case.Insensitive);
    }

    [Fact]
    public void Upholding_a_report_needs_a_statement_of_reasons_and_dismissing_does_not()
    {
        var item = Report();
        _api.ListModerationAsync("open", Arg.Any<CancellationToken>()).Returns([item]);
        _api.DecideAsync(item.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(item with { Status = "decided" });
        var cut = Render<ModerationQueue>();
        cut.WaitForAssertion(() => cut.FindAll("section.case").Count.ShouldBe(1));
        var uphold = () => cut.FindAll("button").First(button => button.TextContent.StartsWith("Retenir", StringComparison.Ordinal));
        uphold().HasAttribute("disabled").ShouldBeTrue();

        cut.Find($"#statement-{item.Id}").Input("Les horaires cités sont faux.");
        uphold().HasAttribute("disabled").ShouldBeFalse();
        uphold().Click();

        cut.WaitForAssertion(() => _api.Received(1).DecideAsync(item.Id, "upheld", "Les horaires cités sont faux.", Arg.Any<CancellationToken>()));
        cut.Find("p.alert.ok").TextContent.ShouldContain("l'élément est retiré");

        cut.FindAll("button").First(button => button.TextContent == "Classer sans suite").Click();
        cut.WaitForAssertion(() => _api.Received(1).DecideAsync(item.Id, "dismissed", "Les horaires cités sont faux.", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Dismissing_without_any_statement_sends_none()
    {
        var item = Report();
        _api.ListModerationAsync("open", Arg.Any<CancellationToken>()).Returns([item]);
        _api.DecideAsync(item.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(item);
        var cut = Render<ModerationQueue>();
        cut.WaitForAssertion(() => cut.FindAll("section.case").Count.ShouldBe(1));

        cut.FindAll("button").First(button => button.TextContent == "Classer sans suite").Click();

        cut.WaitForAssertion(() => _api.Received(1).DecideAsync(item.Id, "dismissed", null, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Decided_reports_show_the_decision_and_its_statement_and_the_filter_asks_for_them()
    {
        _api.ListModerationAsync("open", Arg.Any<CancellationToken>()).Returns([]);
        _api.ListModerationAsync("decided", Arg.Any<CancellationToken>()).Returns([Report("decided", decision: "upheld", statement: "Partenariat rémunéré non déclaré."), Report("decided", decision: "dismissed")]);
        var cut = Render<ModerationQueue>();
        cut.Find("#empty").TextContent.ShouldContain("Aucun signalement");

        cut.Find("#status").Change("decided");

        cut.WaitForAssertion(() => cut.FindAll("section.case.decided").Count.ShouldBe(2));
        cut.Markup.ShouldContain("Signalement retenu : élément retiré");
        cut.Markup.ShouldContain("Partenariat rémunéré non déclaré.");
        cut.Markup.ShouldContain("Classé sans suite");
        cut.FindAll("textarea").ShouldBeEmpty(); // nothing left to decide
    }

    [Fact]
    public void A_decision_already_taken_elsewhere_is_reported_to_the_editor()
    {
        var item = Report();
        _api.ListModerationAsync("open", Arg.Any<CancellationToken>()).Returns([item]);
        _api.DecideAsync(item.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns<ModerationCaseDto>(_ => throw new AdminApiException("Ce signalement a déjà fait l'objet d'une décision.", 409));
        var cut = Render<ModerationQueue>();
        cut.WaitForAssertion(() => cut.FindAll("section.case").Count.ShouldBe(1));

        cut.FindAll("button").First(button => button.TextContent == "Classer sans suite").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("déjà fait l'objet d'une décision"));
    }

    // ---- pure helpers

    [Fact]
    public void Chapters_are_read_and_written_as_in_a_description()
    {
        var (chapters, error) = ChapterText.Parse("02:15 Gordes\n\n 05:40  Roussillon  \n1:02:03 Fin");

        error.ShouldBeNull();
        chapters.ShouldBe([new ChapterDto(135, "Gordes"), new ChapterDto(340, "Roussillon"), new ChapterDto(3723, "Fin")]);
        ChapterText.Format(chapters!).ShouldBe("02:15 Gordes\n05:40 Roussillon\n1:02:03 Fin");
        ChapterText.Parse(null).Chapters.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Gordes")]
    [InlineData("2 Gordes")]
    [InlineData("02:75 Gordes")]
    [InlineData("02:15")]
    [InlineData("aa:bb Gordes")]
    [InlineData("1:2:3:4 Gordes")]
    public void An_unreadable_chapter_line_is_refused(string text) => ChapterText.Parse(text).Error.ShouldNotBeNull();

    [Fact]
    public void Every_reason_the_service_can_give_has_a_french_wording()
    {
        Labels.PublishBlock(null).ShouldBeNull();
        Labels.PublishBlock("terms_required")!.ShouldStartWith("Publication impossible");
        Labels.PublishBlock("specialty_required")!.ShouldContain("spécialité");
        foreach (var reason in ReportReasons.All)
        {
            Labels.ModerationReason(reason).ShouldNotBe(reason);
        }

        foreach (var code in OnVoyage.Taxonomy.Interests.LevelOne)
        {
            Labels.Specialty(code).ShouldNotBe(code);
        }
    }
}

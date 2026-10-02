using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Studio.Api;
using OnVoyage.Web.Studio.Auth;
using OnVoyage.Web.Studio.Components.Pages;

namespace OnVoyage.Web.Studio.Tests;

/// <summary>T-1206: the screens of the creator space, on fixed data (no service behind them).</summary>
public sealed class StudioPageTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PoiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IStudioApi _api = Substitute.For<IStudioApi>();
    private readonly IRoleRefresher _roles = Substitute.For<IRoleRefresher>();

    public StudioPageTests()
    {
        Services.AddSingleton(_api);
        Services.AddSingleton(_roles);
    }

    private BunitAuthorizationContext SignIn(params string[] roles)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("marie@onvoyage.test");
        auth.SetClaims([.. roles.Select(role => new Claim(StudioClaims.Roles, role))]);
        return auth;
    }

    private static StudioProfileDto Profile(string status = "draft", PublishBlockDto? block = null, string[]? specialties = null, int? followers = null, AdminContentDto[]? contents = null, AdminPlaceLinkDto[]? links = null, AdminTipDto[]? tips = null) =>
        new(Guid.NewGuid(), "marie_marseille", "Marie", "Marseillaise.", null, ["fr"], specialties ?? ["history"], [], [], status, false, "2026-10", Now, followers, followers is null, block, contents ?? [], links ?? [], tips ?? []);

    private static AdminContentDto Content(string title = "Marseille en 48 h", string status = "imported", bool commercial = false) =>
        new(Guid.NewGuid(), "youtube", "video", title, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", null, "covers/x.jpg", 600, Now, commercial, status, [new ChapterDto(135, "Fort Saint-Jean")]);

    // ---- join

    [Fact]
    public void The_sign_up_needs_the_terms_to_be_read_and_accepted_and_names_their_version()
    {
        SignIn();
        _api.GetRegistrationAsync(Arg.Any<CancellationToken>()).Returns(new StudioRegistrationDto(false, "2026-10", null, null, null, false));
        _api.SignUpAsync("marie_marseille", "Marie", "2026-10", Arg.Any<CancellationToken>()).Returns(new StudioRegistrationDto(true, "2026-10", Guid.NewGuid(), "marie_marseille", "draft", true));
        _roles.WaitForCreatorRoleAsync(Arg.Any<CancellationToken>()).Returns(true);
        var cut = Render<Join>();

        cut.WaitForAssertion(() => cut.Find("#terms").TextContent.ShouldContain("Publicité"));
        cut.Find("h3").TextContent.ShouldContain("2026-10");
        cut.Find("#terms-notice").TextContent.ShouldContain("provisoire");
        cut.Find("#join-handle").Input("marie_marseille");
        cut.Find("#join-name").Input("Marie");
        cut.Find("#join-submit").HasAttribute("disabled").ShouldBeTrue(); // terms not accepted yet

        cut.Find("#accept-terms").Change(true);
        cut.Find("#join-submit").HasAttribute("disabled").ShouldBeFalse();
        cut.Find("#join-submit").Click();

        cut.WaitForAssertion(() => _api.Received(1).SignUpAsync("marie_marseille", "Marie", "2026-10", Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => _roles.Received(1).WaitForCreatorRoleAsync(Arg.Any<CancellationToken>()));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/studio");
    }

    [Fact]
    public void A_taken_handle_shows_the_suggestion_and_the_role_is_not_waited_for()
    {
        SignIn();
        _api.GetRegistrationAsync(Arg.Any<CancellationToken>()).Returns(new StudioRegistrationDto(false, "2026-10", null, null, null, false));
        _api.SignUpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<StudioRegistrationDto>(_ => throw new StudioApiException("Le handle @marie est déjà pris. Suggestion : @marie2.", 409));
        var cut = Render<Join>();
        cut.WaitForAssertion(() => cut.Find("#join-handle"));

        cut.Find("#join-handle").Input("marie");
        cut.Find("#join-name").Input("Marie");
        cut.Find("#accept-terms").Change(true);
        cut.Find("#join-submit").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("Suggestion : @marie2."));
        _roles.DidNotReceiveWithAnyArgs().WaitForCreatorRoleAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void When_the_role_has_not_arrived_yet_the_creator_is_told_to_try_again()
    {
        SignIn();
        _api.GetRegistrationAsync(Arg.Any<CancellationToken>()).Returns(new StudioRegistrationDto(true, "2026-10", Guid.NewGuid(), "marie", "draft", true));
        _roles.WaitForCreatorRoleAsync(Arg.Any<CancellationToken>()).Returns(false);
        var cut = Render<Join>();

        cut.WaitForAssertion(() => cut.Find("#activate").Click());

        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("réessayez"));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldNotEndWith("/studio");
    }

    // ---- home

    [Fact]
    public void An_account_that_is_not_a_creator_yet_is_sent_to_the_sign_up()
    {
        SignIn();

        Render<Home>();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/studio/join");
        _api.DidNotReceiveWithAnyArgs().GetProfileAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_home_says_what_blocks_the_publication_and_never_a_follower_count_below_twenty()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(block: new PublishBlockDto("specialty_required", "x"), specialties: []));

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.Find("#publish-block").TextContent.ShouldContain("choisissez au moins une spécialité"));
        cut.Find("#followers").TextContent.ShouldBe("Nouveau créateur");
        cut.Find("#status").TextContent.ShouldBe("Brouillon (non publié)");
    }

    [Fact]
    public void From_twenty_followers_the_home_shows_the_count_and_a_suspension_is_explained()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile("suspended", followers: 25));

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.Find("#followers").TextContent.ShouldBe("25 abonnés"));
        cut.Find("#suspended").TextContent.ShouldContain("suspendue");
    }

    // ---- profile

    [Fact]
    public void Without_a_specialty_the_publish_button_is_disabled_with_the_french_reason()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(block: new PublishBlockDto("specialty_required", "x"), specialties: []));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => cut.Find("#publish"));
        cut.Find("#publish").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#publish").GetAttribute("aria-describedby").ShouldBe("publish-block");
        cut.Find("#publish-block").TextContent.ShouldBe("Publication impossible : choisissez au moins une spécialité.");
    }

    [Fact]
    public void Choosing_a_specialty_saves_the_profile_and_publishing_puts_the_page_online_with_the_handle_locked()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(block: new PublishBlockDto("specialty_required", "x"), specialties: []));
        _api.UpdateProfileAsync(Arg.Any<CreatorProfileRequest>(), Arg.Any<CancellationToken>()).Returns(Profile(specialties: ["nature"]));
        _api.PublishAsync(Arg.Any<CancellationToken>()).Returns(Profile("published", specialties: ["nature"]));
        var cut = Render<Profile>();
        cut.WaitForAssertion(() => cut.Find("#publish"));

        cut.Find("input[name=specialty][value=nature]").Change(true);
        cut.Find("#profile-submit").Click();
        cut.WaitForAssertion(() => _api.Received(1).UpdateProfileAsync(
            Arg.Is<CreatorProfileRequest>(request => request.Handle == "marie_marseille" && request.Specialties!.Contains("nature")), Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("#publish").HasAttribute("disabled").ShouldBeFalse());

        cut.Find("#publish").Click();

        cut.WaitForAssertion(() => cut.Find("#status").TextContent.ShouldBe("En ligne"));
        cut.Find("#profile-handle").HasAttribute("disabled").ShouldBeTrue();
        cut.FindAll("#publish").ShouldBeEmpty();
        cut.Find("#unpublish").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void A_suspended_creator_can_read_the_profile_but_not_edit_nor_publish()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile("suspended"));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => cut.Find("#suspended"));
        cut.Find("#publish").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#profile-submit").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void A_refusal_of_the_service_is_shown_as_it_is()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile());
        _api.PublishAsync(Arg.Any<CancellationToken>()).Returns<StudioProfileDto>(_ => throw new StudioApiException("Les CGU créateurs ne sont pas acceptées.", 422));
        var cut = Render<Profile>();
        cut.WaitForAssertion(() => cut.Find("#publish"));

        cut.Find("#publish").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("ne sont pas acceptées"));
        cut.Find("#status").TextContent.ShouldBe("Brouillon (non publié)");
    }

    // ---- tips

    [Fact]
    public void A_tip_is_written_for_a_place_found_in_the_directory_and_limited_to_280_characters()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile());
        _api.SearchPlacesAsync("fort", Arg.Any<CancellationToken>()).Returns([new PoiSearchResultDto(PoiId, "Fort Saint-Jean", "Marseille", "marseille", true)]);
        _api.SetTipAsync(PoiId, "Viens au coucher du soleil.", Arg.Any<CancellationToken>()).Returns(new AdminTipDto(Guid.NewGuid(), PoiId, "Fort Saint-Jean", "Viens au coucher du soleil.", "published", Now));
        var cut = Render<Tips>();
        cut.WaitForAssertion(() => cut.Find("#no-tips"));

        cut.Find("#place-query").Input("fort");
        cut.Find("#search-places").Click();
        cut.WaitForAssertion(() => cut.Find("#place-results").TextContent.ShouldContain("Fort Saint-Jean"));
        cut.Find("#place-results button").Click();
        cut.Find("#tip").GetAttribute("maxlength").ShouldBe("280");
        cut.Find("#save-tip").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#tip").Input("Viens au coucher du soleil.");
        cut.Find("#save-tip").Click();

        cut.WaitForAssertion(() => _api.Received(1).SetTipAsync(PoiId, "Viens au coucher du soleil.", Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldBe("Conseil enregistré."));
    }

    [Fact]
    public void Tips_are_listed_and_a_tip_hidden_by_moderation_is_flagged_and_can_be_deleted()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(tips: [new AdminTipDto(Guid.NewGuid(), PoiId, "Fort Saint-Jean", "Au coucher du soleil.", "hidden", Now)]));
        var cut = Render<Tips>();

        cut.WaitForAssertion(() => cut.Find("li.tip").TextContent.ShouldContain("masqué par la modération"));
        cut.Find("li.tip button").Click();

        cut.WaitForAssertion(() => _api.Received(1).RemoveTipAsync(PoiId, Arg.Any<CancellationToken>()));
    }

    // ---- contents

    [Fact]
    public void A_content_is_added_by_its_url_with_chapters_and_the_commercial_flag()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile());
        _api.AddContentAsync(Arg.Any<AddContentRequest>(), Arg.Any<CancellationToken>()).Returns(Content());
        var cut = Render<Contents>();
        cut.WaitForAssertion(() => cut.Find("#no-contents"));
        cut.Find("#add-content").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#content-url").Input("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        cut.Find("#content-title").Input("Marseille en 48 h");
        cut.Find("#content-commercial").Change(true);
        cut.Find("#content-chapters").Change("02:15 Fort Saint-Jean\n05:40 Notre-Dame de la Garde");
        cut.Find("#add-content").Click();

        cut.WaitForAssertion(() => _api.Received(1).AddContentAsync(
            Arg.Is<AddContentRequest>(request => request.Url.EndsWith("dQw4w9WgXcQ", StringComparison.Ordinal) && request.IsCommercial && request.Chapters!.Count == 2 && request.Chapters[0].StartSeconds == 135),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void An_unreadable_chapter_is_refused_before_anything_is_sent()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile());
        var cut = Render<Contents>();
        cut.WaitForAssertion(() => cut.Find("#add-content"));

        cut.Find("#content-url").Input("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        cut.Find("#content-title").Input("Marseille");
        cut.Find("#content-chapters").Change("Gordes sans heure");
        cut.Find("#add-content").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.error").TextContent.ShouldContain("Chapitre illisible"));
        _api.DidNotReceiveWithAnyArgs().AddContentAsync(default!, Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_content_can_be_hidden_marked_as_an_ad_and_removed_and_its_places_are_validated_by_the_creator()
    {
        SignIn("creator");
        var content = Content();
        var link = new AdminPlaceLinkDto(Guid.NewGuid(), PoiId, "Fort Saint-Jean", content.Id, content.Title, 135, 0.95, "proposed", null);
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile(contents: [content], links: [link]));
        _api.UpdateContentAsync(content.Id, Arg.Any<UpdateContentRequest>(), Arg.Any<CancellationToken>()).Returns(content);
        _api.SetPlaceLinkStatusAsync(link.Id, "validated", Arg.Any<CancellationToken>()).Returns(link with { Status = "validated" });
        var cut = Render<Contents>();
        cut.WaitForAssertion(() => cut.Find("tr.content"));

        cut.FindAll("tr.content button").First(button => button.TextContent == "Masquer").Click();
        cut.WaitForAssertion(() => _api.Received(1).UpdateContentAsync(content.Id, Arg.Is<UpdateContentRequest>(request => request.Status == "hidden"), Arg.Any<CancellationToken>()));
        cut.FindAll("tr.content button").First(button => button.TextContent == "Marquer Publicité").Click();
        cut.WaitForAssertion(() => _api.Received(1).UpdateContentAsync(content.Id, Arg.Is<UpdateContentRequest>(request => request.IsCommercial), Arg.Any<CancellationToken>()));
        cut.Find("tr.link").TextContent.ShouldContain("Proposée (non publiée)");
        cut.Find("tr.link").TextContent.ShouldContain("02:15");
        cut.FindAll("tr.link button").First(button => button.TextContent == "Valider").Click();

        cut.WaitForAssertion(() => _api.Received(1).SetPlaceLinkStatusAsync(link.Id, "validated", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_suspended_creator_cannot_change_a_content()
    {
        SignIn("creator");
        _api.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(Profile("suspended", contents: [Content()]));

        var cut = Render<Contents>();

        cut.WaitForAssertion(() => cut.Find("tr.content"));
        cut.FindAll("tr.content button").ShouldAllBe(button => button.HasAttribute("disabled"));
    }
}

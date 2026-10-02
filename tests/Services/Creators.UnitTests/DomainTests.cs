using OnVoyage.Creators.Domain;

namespace Creators.UnitTests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CreatorProfile Profile(params string[] specialties) =>
        new("marie_marseille", "Marie", null, null, ["fr"], specialties, [], []);

    [Theory]
    [InlineData("marie", true)]
    [InlineData("@marie", true)]
    [InlineData("  @Marie.Voyage_1 ", true)]
    [InlineData("ma", false)] // 3 characters at least
    [InlineData("abcdefghijklmnopqrstuvwxyz01234", false)] // 31
    [InlineData("marie voyage", false)]
    [InlineData("marie-voyage", false)]
    [InlineData(".marie", false)]
    [InlineData("marie.", false)]
    [InlineData("ma..rie", false)]
    [InlineData("", false)]
    public void A_handle_is_3_to_30_letters_digits_dots_and_underscores(string input, bool valid) =>
        Handles.TryNormalize(input, out _).ShouldBe(valid);

    [Fact]
    public void The_handle_keeps_its_case_for_display_and_loses_the_at_sign()
    {
        Handles.TryNormalize("@Marie", out var handle).ShouldBeTrue();
        handle.ShouldBe("Marie");
    }

    [Fact]
    public void A_taken_handle_gets_the_first_free_variant_within_30_characters()
    {
        HashSet<string> taken = ["marie", "marie2", "marie3"];
        Handles.Suggest("marie", candidate => taken.Contains(candidate)).ShouldBe("marie4");

        var longest = new string('a', 30);
        var suggestion = Handles.Suggest(longest, candidate => candidate == longest);
        suggestion.Length.ShouldBe(30);
        suggestion.ShouldEndWith("2");
    }

    [Fact]
    public void A_creator_without_terms_nor_founder_consent_cannot_be_published()
    {
        var creator = Creator.NewDraft(Guid.NewGuid(), Profile("nature"), founding: true, Now);

        creator.TermsAccepted.ShouldBeFalse();
        creator.PublishBlock!.Code.ShouldBe("terms_required");
    }

    [Fact]
    public void The_founder_consent_needs_its_document_reference()
    {
        var creator = Creator.NewDraft(Guid.NewGuid(), Profile("nature"), founding: true, Now);

        creator.WithFounderConsent("   ", Now, Now).TermsAccepted.ShouldBeFalse();

        var consenting = creator.WithFounderConsent("H-009/marie-2026-09-30.pdf", Now, Now);
        consenting.TermsAccepted.ShouldBeTrue();
        consenting.TermsVersion.ShouldBe("fondateur");
        consenting.TermsDocumentRef.ShouldBe("H-009/marie-2026-09-30.pdf");
        consenting.PublishBlock.ShouldBeNull();
    }

    [Fact]
    public void Accepted_terms_of_a_regular_creator_are_enough_without_a_document()
    {
        var creator = Creator.NewDraft(Guid.NewGuid(), Profile("nature"), founding: false, Now) with { TermsVersion = "2026-10", TermsAcceptedAt = Now };

        creator.TermsAccepted.ShouldBeTrue();
        creator.PublishBlock.ShouldBeNull();
    }

    [Fact]
    public void At_least_one_specialty_is_needed_even_with_consent()
    {
        var creator = Creator.NewDraft(Guid.NewGuid(), Profile(), founding: true, Now).WithFounderConsent("doc", Now, Now);

        creator.PublishBlock!.Code.ShouldBe("specialty_required");
    }

    [Fact]
    public void A_profile_is_normalized_and_checked()
    {
        var (profile, violation) = CreatorProfileRules.Normalize(new CreatorProfile(" @Marie ", " Marie G. ", " ", null, ["FR", "fr", "en"], ["nature", "history.military", "nature"], [], [new CreatorLink("Instagram", "https://www.instagram.com/marie")]));

        violation.ShouldBeNull();
        profile!.Handle.ShouldBe("Marie");
        profile.DisplayName.ShouldBe("Marie G.");
        profile.Bio.ShouldBeNull();
        profile.Languages.ShouldBe(["fr", "en"]);
        profile.Specialties.ShouldBe(["nature", "history.military"]);
        profile.Links.ShouldBe([new CreatorLink("instagram", "https://www.instagram.com/marie")]);
    }

    [Theory]
    [InlineData("ab", "Marie", null, "nature", "invalid_handle")]
    [InlineData("marie", "", null, "nature", "validation")]
    [InlineData("marie", "Marie", "x", "dragons", "validation")] // unknown taxonomy code
    public void A_bad_profile_is_refused(string handle, string name, string? bio, string specialty, string code)
    {
        var (profile, violation) = CreatorProfileRules.Normalize(new CreatorProfile(handle, name, bio, null, [], [specialty], [], []));

        profile.ShouldBeNull();
        violation!.Code.ShouldBe(code);
    }

    [Fact]
    public void The_bio_is_300_characters_and_specialties_are_5_at_most()
    {
        CreatorProfileRules.Normalize(Profile("nature") with { Bio = new string('x', 301) }).Violation.ShouldNotBeNull();
        CreatorProfileRules.Normalize(Profile("nature") with { Bio = new string('x', 300) }).Violation.ShouldBeNull();
        CreatorProfileRules.Normalize(Profile("nature", "history", "culture", "religion", "villages", "gastronomy")).Violation.ShouldNotBeNull();
        CreatorProfileRules.Normalize(Profile("nature") with { Links = [new CreatorLink("instagram", "http://insecure.example")] }).Violation.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s", "youtube", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "youtube", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "youtube", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.instagram.com/reel/C1a2B3c4D5e/?igsh=zzz", "instagram", "C1a2B3c4D5e", "https://www.instagram.com/reel/C1a2B3c4D5e/")]
    [InlineData("https://instagram.com/p/C1a2B3c4D5e", "instagram", "C1a2B3c4D5e", "https://www.instagram.com/p/C1a2B3c4D5e/")]
    [InlineData("https://www.tiktok.com/@marie.voyage/video/7301234567890123456", "tiktok", "7301234567890123456", "https://www.tiktok.com/@marie.voyage/video/7301234567890123456")]
    public void The_url_of_a_content_gives_its_platform_identifier_and_canonical_link(string url, string platform, string id, string permalink)
    {
        var parsed = ContentUrls.Parse(url);

        parsed.ShouldNotBeNull();
        (parsed.Platform, parsed.ExternalId, parsed.Permalink).ShouldBe((platform, id, permalink));
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")] // https only
    [InlineData("https://www.youtube.com/channel/UC123")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://www.tiktok.com/@marie/photo/123456")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Anything_else_is_not_a_content_reference(string url) => ContentUrls.Parse(url).ShouldBeNull();

    [Fact]
    public void A_chapter_link_to_a_youtube_video_is_timestamped_and_other_links_are_left_alone()
    {
        ContentUrls.At("youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", 135).ShouldBe("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=135s");
        ContentUrls.At("youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", null).ShouldBe("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        ContentUrls.At("youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", 0).ShouldBe("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        ContentUrls.At("instagram", "https://www.instagram.com/reel/C1a2B3c4D5e/", 30).ShouldBe("https://www.instagram.com/reel/C1a2B3c4D5e/");
    }

    [Fact]
    public void Chapters_are_ordered_and_must_be_distinct_titled_and_inside_the_duration()
    {
        var (ordered, _) = ContentRules.NormalizeChapters([new Chapter(340, " Roussillon "), new Chapter(135, "Gordes")], 600);
        ordered.ShouldBe([new Chapter(135, "Gordes"), new Chapter(340, "Roussillon")]);

        ContentRules.NormalizeChapters([new Chapter(10, "a"), new Chapter(10, "b")], null).Chapters.ShouldBeNull();
        ContentRules.NormalizeChapters([new Chapter(10, "")], null).Chapters.ShouldBeNull();
        ContentRules.NormalizeChapters([new Chapter(700, "late")], 600).Chapters.ShouldBeNull();
        ContentRules.NormalizeChapters([new Chapter(-1, "early")], null).Chapters.ShouldBeNull();
        ContentRules.NormalizeChapters(null, null).Chapters.ShouldBeEmpty();
    }

    [Fact]
    public void A_tip_is_280_characters_at_most()
    {
        CreatorTip.Check(new string('x', 280)).ShouldBeNull();
        CreatorTip.Check(new string('x', 281)).ShouldNotBeNull();
        CreatorTip.Check("  ").ShouldNotBeNull();
        CreatorTip.Check(null).ShouldNotBeNull();
    }

    [Fact]
    public void A_place_link_is_validated_once_and_keeps_the_first_validation_date()
    {
        var link = new PlaceLink(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 0.8, "proposed", null, Now);

        var validated = link.WithStatus("validated", Now.AddHours(1));
        validated.IsValidated.ShouldBeTrue();
        validated.ValidatedAt.ShouldBe(Now.AddHours(1));
        validated.WithStatus("validated", Now.AddDays(1)).ValidatedAt.ShouldBe(Now.AddHours(1));
        validated.WithStatus("rejected", Now.AddDays(1)).ValidatedAt.ShouldBeNull();
    }

    [Fact]
    public void An_upheld_decision_needs_a_statement_of_reasons_and_a_case_is_decided_once()
    {
        var report = ModerationCase.Open(Guid.NewGuid(), "tip", Guid.NewGuid(), "inaccurate", Guid.NewGuid(), Now);

        report.Decide("upheld", "  ", Now).Violation!.Code.ShouldBe("statement_required");
        report.Decide("maybe", "x", Now).Violation!.Code.ShouldBe("validation");

        var dismissed = report.Decide("dismissed", null, Now).Case!;
        dismissed.Status.ShouldBe("decided");
        dismissed.DecidedAt.ShouldBe(Now);
        dismissed.Decide("upheld", "again", Now).Violation!.Code.ShouldBe("case_closed");

        report.Decide("upheld", " L'adresse indiquée est fausse. ", Now).Case!.StatementOfReasons.ShouldBe("L'adresse indiquée est fausse.");
    }

    [Fact]
    public void The_place_directory_search_text_ignores_case_and_accents_and_holds_no_position()
    {
        var entry = new PoiEntry(Guid.NewGuid(), Guid.NewGuid(), "marseille", "Notre-Dame de la Garde", "Our Lady of the Guard", ["La Bonne Mère"], "Marseille", 90, true, 1);

        entry.SearchText.ShouldBe("notre-dame de la garde our lady of the guard marseille la bonne mere");
        PoiEntry.Fold("  Église Saint-Étienne ").ShouldBe("eglise saint-etienne");
        typeof(PoiEntry).GetProperties().Select(property => property.Name).ShouldNotContain(name => name.Contains("Lat", StringComparison.Ordinal) || name.Contains("Lon", StringComparison.Ordinal) || name.Contains("Location", StringComparison.Ordinal));
    }
}

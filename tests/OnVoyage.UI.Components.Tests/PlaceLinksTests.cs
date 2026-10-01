using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class PlaceLinksTests : BunitContext
{
    private IRenderedComponent<Place> Show(params LinkDto[]? links)
    {
        var id = Guid.CreateVersion7();
        var catalog = Substitute.For<ICatalogClient>();
        catalog.GetPoiAsync("fort", Arg.Any<CancellationToken>()).Returns(new PoiDetailDto(id, "fort", "Fort Saint-Jean", "history", 43.29, 5.36, 0.8, 2, false, [], ["© OpenStreetMap contributors"], links));
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([]);
        Services.AddSingleton(catalog);
        var profiles = new InMemoryProfileStore();
        Services.AddSingleton<IProfileStore>(profiles);
        this.AddLearning(profiles);
        return Render<Place>(parameters => parameters.Add(p => p.Slug, "fort"));
    }

    [Fact]
    public void Links_open_outside_the_app_and_a_video_shows_our_own_thumbnail()
    {
        var cut = Show(
            new LinkDto("wikipedia", "fr", "Fort Saint-Jean", "https://fr.wikipedia.org/wiki/Fort_Saint-Jean", null, null, null),
            new LinkDto("youtube", "fr", "Le fort raconté", "https://www.youtube.com/watch?v=abcdefghijk", "Chaîne Marseille", "https://media.on.voyage/media/thumbs/x/abcdefghijk.jpg", "abcdefghijk"));

        cut.WaitForAssertion(() =>
        {
            var anchors = cut.FindAll(".learn-more a");
            anchors.Count.ShouldBe(2);
            anchors.ShouldAllBe(anchor => anchor.GetAttribute("target") == "_blank" && anchor.GetAttribute("rel") == "noopener noreferrer");
            anchors[1].TextContent.ShouldContain("Vidéo : Le fort raconté (Chaîne Marseille)");
            cut.Find(".learn-more img").GetAttribute("src").ShouldBe("https://media.on.voyage/media/thumbs/x/abcdefghijk.jpg");
        });
    }

    [Fact]
    public void Nothing_from_youtube_is_embedded_or_loaded_by_the_page()
    {
        var cut = Show(new LinkDto("youtube", "fr", "Le fort", "https://www.youtube.com/watch?v=abcdefghijk", "Chaîne", "/media/thumbs/x/abcdefghijk.jpg", "abcdefghijk"));

        cut.WaitForAssertion(() => cut.FindAll(".learn-more a").Count.ShouldBe(1));
        cut.FindAll("iframe, embed, object, video, script").ShouldBeEmpty();
        cut.FindAll("img").ShouldAllBe(image => !image.GetAttribute("src")!.Contains("ytimg", StringComparison.Ordinal) && !image.GetAttribute("src")!.Contains("youtube", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_place_without_links_shows_no_learn_more_section(int mode)
    {
        var cut = mode == 0 ? Show([]) : Show(null);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Fort Saint-Jean"));
        cut.FindAll(".learn-more").ShouldBeEmpty();
    }
}

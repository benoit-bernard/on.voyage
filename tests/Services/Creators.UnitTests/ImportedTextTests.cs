using OnVoyage.Creators.Domain;

namespace Creators.UnitTests;

public sealed class ImportedTextTests
{
    // ---- chapters (T-1208: ten formats)

    [Theory]
    [InlineData("00:00 Intro", 0, "Intro")]
    [InlineData("0:00 Intro", 0, "Intro")]
    [InlineData("02:15 Gordes", 135, "Gordes")]
    [InlineData("2:15 Gordes", 135, "Gordes")]
    [InlineData("1:02:15 Roussillon", 3735, "Roussillon")]
    [InlineData("02:15 - Gordes", 135, "Gordes")]
    [InlineData("02:15 – Gordes", 135, "Gordes")]
    [InlineData("02:15 — Gordes", 135, "Gordes")]
    [InlineData("02:15: Gordes", 135, "Gordes")]
    [InlineData("[02:15] Gordes", 135, "Gordes")]
    [InlineData("(02:15) Gordes", 135, "Gordes")]
    [InlineData("• 02:15 Gordes", 135, "Gordes")]
    [InlineData("- 02:15 Gordes", 135, "Gordes")]
    [InlineData("▶ 02:15 Gordes", 135, "Gordes")]
    [InlineData("Gordes - 02:15", 135, "Gordes")]
    [InlineData("Gordes (02:15)", 135, "Gordes")]
    [InlineData("  02:15   Gordes et son village  ", 135, "Gordes et son village")]
    public void A_chapter_line_is_read_in_the_formats_creators_use(string line, int seconds, string title)
    {
        var chapters = ChapterParser.Parse("Salut tout le monde\n" + line + "\nMerci !");

        chapters.ShouldBe([new Chapter(seconds, title)]);
    }

    [Fact]
    public void Chapters_of_a_description_come_in_time_order_without_duplicates()
    {
        var description = "Mes étapes :\n05:40 Roussillon\n00:00 Intro\n02:15 Gordes\n02:15 Gordes bis\nabonnez-vous https://example.org/a:b\n";

        var chapters = ChapterParser.Parse(description);

        chapters.Select(chapter => (chapter.StartSeconds, chapter.Title)).ShouldBe([(0, "Intro"), (135, "Gordes"), (340, "Roussillon")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Pas de chapitres ici")]
    [InlineData("Rendez-vous à 12h30 devant la gare")]
    [InlineData("99:99 impossible")]
    [InlineData("12:75 secondes trop grandes")]
    public void A_description_without_a_timestamp_has_no_chapters(string? description) => ChapterParser.Parse(description).ShouldBeEmpty();

    [Fact]
    public void A_chapter_after_the_end_of_the_video_is_ignored_and_a_long_title_is_cut()
    {
        var chapters = ChapterParser.Parse($"00:10 Début\n20:00 Après la fin\n00:30 {new string('x', 150)}", durationSeconds: 600);

        chapters.Select(chapter => chapter.StartSeconds).ShouldBe([10, 30]);
        chapters[1].Title.Length.ShouldBe(100);
    }

    [Fact]
    public void No_more_than_the_maximum_number_of_chapters_is_kept()
    {
        var description = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"{i / 60:00}:{i % 60:00} Étape {i}"));

        ChapterParser.Parse(description).Count.ShouldBe(ContentRules.MaxChapters);
    }

    // ---- durations

    [Theory]
    [InlineData("PT45S", 45)]
    [InlineData("PT12M34S", 754)]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("PT1H", 3600)]
    [InlineData("P1DT1H", 90000)]
    [InlineData("P0D", null)]
    [InlineData("PT0S", null)]
    [InlineData("not a duration", null)]
    [InlineData(null, null)]
    public void An_iso_duration_becomes_seconds(string? text, int? expected) => IsoDuration.ToSeconds(text).ShouldBe(expected);

    // ---- #publicité

    [Theory]
    [InlineData("Belle balade #marseille #publicité")]
    [InlineData("Belle balade #PUBLICITE")]
    [InlineData("Belle balade #ad")]
    [InlineData("#Ad en plein soleil")]
    [InlineData("En partenariat avec l'office #sponsorisé")]
    [InlineData("#sponsorisée par la ville")]
    [InlineData("#sponsored")]
    [InlineData("Merci à la marque ! #collaboration_commerciale")]
    [InlineData("#partenariatrémunéré")]
    public void A_commercial_hashtag_is_detected_whatever_its_case_and_accents(string text) => CommercialDisclosure.Detects(text).ShouldBeTrue();

    [Theory]
    [InlineData("Une aventure #adventure #addict")]
    [InlineData("Je suis un ad hoc de la pub sans hashtag")]
    [InlineData("publicité sans dièse")]
    [InlineData("#marseille #voyage")]
    [InlineData("")]
    [InlineData(null)]
    public void Whole_hashtags_only_a_word_or_a_longer_hashtag_is_not_an_ad(string? text) => CommercialDisclosure.Detects(text).ShouldBeFalse();

    [Fact]
    public void The_title_and_the_caption_are_both_read() => CommercialDisclosure.Detects("Mon voyage", "Merci #sponso").ShouldBeTrue();
}

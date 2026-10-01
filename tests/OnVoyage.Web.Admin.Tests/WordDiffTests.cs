using OnVoyage.Web.Admin.Text;

namespace OnVoyage.Web.Admin.Tests;

public sealed class WordDiffTests
{
    [Fact]
    public void Identical_texts_are_one_unchanged_part()
    {
        var parts = WordDiff.Compare("Le fort garde le port.", "Le fort garde le port.");

        parts.ShouldHaveSingleItem().ShouldBe(new DiffPart(DiffKind.Same, "Le fort garde le port."));
    }

    [Fact]
    public void A_replaced_word_is_shown_as_removed_then_added_between_unchanged_words()
    {
        var parts = WordDiff.Compare("Le fort date de 1660 environ.", "Le fort date de 1668 environ.");

        parts.Select(part => (part.Kind, part.Text)).ShouldBe(
        [
            (DiffKind.Same, "Le fort date de"),
            (DiffKind.Removed, "1660"),
            (DiffKind.Added, "1668"),
            (DiffKind.Same, "environ."),
        ]);
    }

    [Fact]
    public void Added_and_deleted_words_are_reported_where_they_occur()
    {
        var parts = WordDiff.Compare("un deux trois", "un trois quatre");

        parts.Select(part => (part.Kind, part.Text)).ShouldBe(
        [
            (DiffKind.Same, "un"),
            (DiffKind.Removed, "deux"),
            (DiffKind.Same, "trois"),
            (DiffKind.Added, "quatre"),
        ]);
    }

    [Fact]
    public void Rebuilding_either_side_from_the_parts_gives_the_original_words()
    {
        const string before = "Au pied du port le visiteur longe les remparts et découvre un détail";
        const string after = "Au pied du vieux port le visiteur découvre les remparts puis un détail";

        var parts = WordDiff.Compare(before, after);

        string.Join(' ', parts.Where(part => part.Kind != DiffKind.Added).Select(part => part.Text)).ShouldBe(before);
        string.Join(' ', parts.Where(part => part.Kind != DiffKind.Removed).Select(part => part.Text)).ShouldBe(after);
    }

    [Fact]
    public void Empty_sides_are_handled()
    {
        WordDiff.Compare(string.Empty, string.Empty).ShouldBeEmpty();
        WordDiff.Compare(string.Empty, "nouveau").ShouldHaveSingleItem().Kind.ShouldBe(DiffKind.Added);
        WordDiff.Compare("ancien", string.Empty).ShouldHaveSingleItem().Kind.ShouldBe(DiffKind.Removed);
    }

    [Fact]
    public void Very_long_texts_are_reported_as_replaced_instead_of_building_a_huge_table()
    {
        var long1 = string.Join(' ', Enumerable.Repeat("a", WordDiff.MaxWords + 1));
        var long2 = string.Join(' ', Enumerable.Repeat("b", 10));

        var parts = WordDiff.Compare(long1, long2);

        parts.Select(part => part.Kind).ShouldBe([DiffKind.Removed, DiffKind.Added]);
    }
}

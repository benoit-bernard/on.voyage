using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using OnVoyage.Catalog.Application.Features.SearchPois;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Domain;
using OnVoyage.Catalog.Infrastructure.Persistence;
using OnVoyage.Catalog.Infrastructure.Seed;

namespace Catalog.UnitTests;

public sealed class SearchTests
{
    private readonly IPoiReader _reader = Substitute.For<IPoiReader>();
    private readonly IValidator<SearchPoisQuery> _validator = new SearchPoisValidator();

    private static Poi Sample(string slug) => MarseilleSeed.Pois.First(poi => poi.Slug == slug);

    [Fact]
    public async Task The_text_is_trimmed_and_whitespace_collapsed_before_it_reaches_the_reader()
    {
        _reader.SearchPublishedAsync("marseille", "cathedrale de la major", 20, Arg.Any<CancellationToken>()).Returns([Sample("cathedrale-de-la-major")]);

        var result = await SearchPoisHandler.Handle(new SearchPoisQuery("marseille", "  cathedrale   de la major "), _reader, _validator, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.ShouldHaveSingleItem().Slug.ShouldBe("cathedrale-de-la-major");
        result.Value[0].DistanceMeters.ShouldBeNull("a search has no position");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("   ")]
    public async Task Fewer_than_two_characters_is_rejected_without_querying(string text)
    {
        var result = await SearchPoisHandler.Handle(new SearchPoisQuery("marseille", text), _reader, _validator, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("validation");
        await _reader.DidNotReceiveWithAnyArgs().SearchPublishedAsync(default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task The_limit_is_bounded(int limit)
    {
        var result = await SearchPoisHandler.Handle(new SearchPoisQuery("marseille", "fort", limit), _reader, _validator, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_very_long_text_is_rejected()
    {
        var result = await SearchPoisHandler.Handle(new SearchPoisQuery("marseille", new string('a', 101)), _reader, _validator, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Like_wildcards_typed_by_the_traveler_are_escaped()
    {
        PoiSearch.EscapeLike("100%_\\x").ShouldBe("100\\%\\_\\\\x");
    }

    [Fact]
    public void The_query_translates_to_unaccent_trigram_and_full_text_sql_with_parameters_only()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql("Host=localhost;Database=none", npgsql => npgsql.UseNetTopologySuite()).Options;
        using var db = new CatalogDbContext(options);

        var full = PoiSearch.Build(db, "marseille", "cathedrale'; drop table x;--", "fr").Take(20).ToQueryString();

        // ToQueryString lists the parameter values in leading comment lines; the statement itself is what remains.
        var sql = string.Join('\n', full.Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)));

        sql.ShouldContain("unaccent(");
        sql.ShouldContain("word_similarity(");
        sql.ShouldContain("@contains");
        sql.ShouldContain("@term");
        sql.ToUpperInvariant().ShouldContain("ILIKE");
        sql.ToUpperInvariant().ShouldContain("@@");
        sql.ShouldContain("ORDER BY");
        sql.ShouldNotContain("drop table", Case.Insensitive, "the typed text is a parameter, never part of the SQL");
    }
}

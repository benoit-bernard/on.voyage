using FluentValidation;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.Catalog.Application.Features.SearchPois;

/// <summary>
/// Full-text search over the published places of a destination (F-14): name, short description and keywords, without regard to case or accents.
/// The text is typed by the traveler: it only travels in the <c>q</c> parameter of this call and is neither logged nor stored.
/// </summary>
public sealed record SearchPoisQuery(string Destination, string Text, int Limit = 20)
{
    public const int MinLength = 2;
    public const int MaxLength = 100;
}

public sealed class SearchPoisValidator : AbstractValidator<SearchPoisQuery>
{
    public SearchPoisValidator()
    {
        RuleFor(query => query.Destination).NotEmpty().MaximumLength(64);
        RuleFor(query => query.Text).NotNull().Must(text => Normalize(text).Length >= SearchPoisQuery.MinLength).WithMessage("at least 2 characters");
        RuleFor(query => query.Text).MaximumLength(SearchPoisQuery.MaxLength);
        RuleFor(query => query.Limit).InclusiveBetween(1, 50);
    }

    internal static string Normalize(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public static class SearchPoisHandler
{
    public static async Task<Result<IReadOnlyList<PoiSummaryDto>>> Handle(
        SearchPoisQuery query,
        IPoiReader reader,
        IValidator<SearchPoisQuery> validator,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<IReadOnlyList<PoiSummaryDto>>("validation", validation.Errors[0].ErrorMessage);
        }

        var places = await reader.SearchPublishedAsync(query.Destination, SearchPoisValidator.Normalize(query.Text), query.Limit, cancellationToken);
        return Result.Success<IReadOnlyList<PoiSummaryDto>>([.. places.Select(poi => GetNearbyPoisHandler.Map(poi, null))]);
    }
}

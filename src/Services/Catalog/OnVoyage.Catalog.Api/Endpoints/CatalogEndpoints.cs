using OnVoyage.Catalog.Application;
using OnVoyage.Catalog.Application.Features.GetDestination;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Application.Features.GetPoi;
using OnVoyage.Catalog.Application.Features.SearchPois;
using OnVoyage.Catalog.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Catalog.Api.Endpoints;

internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Reads are open to any traveler session (anonymous included) and to internal hosts such as Web.Public (§12.1, SEC-03).
        var group = app.MapGroup("/api/catalog/v1").RequireAuthorization(Policies.TravelerOrInternal);

        group.MapGet("/destinations/{slug}", (string slug, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<DestinationDto>>(new GetDestinationQuery(slug), ct)));

        group.MapGet("/destinations/{slug}/pois", (string slug, double? lat, double? lon, int? radius, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PoiSummaryDto>>>(
                new GetNearbyPoisQuery(slug, lat, lon, radius ?? 50_000, limit ?? 100), ct)));

        group.MapGet("/search", (string? q, string? destination, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PoiSummaryDto>>>(new SearchPoisQuery(destination ?? "marseille", q ?? string.Empty, limit ?? 20), ct)));

        group.MapGet("/pois/{slug}", (string slug, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<PoiDetailDto>>(new GetPoiQuery(slug), ct)));

        return app;
    }

    private static async Task<IResult> Translate<T>(Task<Result<T>> pending)
    {
        var result = await pending;
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        var error = result.Error!;
        var status = error.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        return Results.Problem(
            title: error.Message,
            statusCode: status,
            type: $"https://on.voyage/problems/{error.Code}");
    }
}

using OnVoyage.Insights.Application;
using OnVoyage.Insights.Application.Features.Events;
using OnVoyage.Insights.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Insights.Api.Endpoints.Events;

internal static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/insights/v1/events", async (EventBatchRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            {
                var traveler = http.User.TravelerId() ?? throw new InvalidOperationException("Authenticated request without a traveler id.");
                return Problems.Translate(await bus.InvokeAsync<Result<EventBatchResponse>>(new IngestEventsCommand(traveler, request.Events ?? []), ct));
            })
            .RequireAuthorization(Policies.Traveler);

        return app;
    }
}

internal static class Problems
{
    public static IResult Translate<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        var error = result.Error!;
        var status = error.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        return Results.Problem(title: error.Message, statusCode: status, type: $"https://on.voyage/problems/{error.Code}");
    }
}

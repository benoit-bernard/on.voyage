using System.Security.Claims;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.Places;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Factory.Api.Endpoints;

internal sealed record UnpublishRequest(string Reason);

internal sealed record EditorialRequest(int? ImportanceOverride, bool? EditoriallySaturated);

internal sealed record DestinationRequest(string Destination);

internal sealed record DecideFactRequest(bool Accept, string? Reason);

internal sealed record WriteStoryRequest(string Lang, StoryKind Kind);

internal sealed record EditStoryRequest(string Title, string Text);

internal sealed record ApproveStoryRequest(double? EditorialScore);

internal sealed record ReasonRequest(string Reason);

internal sealed record ReportRequest(string Reason);

internal static class FactoryEndpoints
{
    public static IEndpointRouteBuilder MapFactoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything in Factory is back-office: no traveler route exists (§9.3).
        var admin = app.MapGroup("/api/factory/v1/admin").RequireAuthorization(Policies.Admin);

        // Long jobs go to the worker through the durable queue; the call returns once the job is recorded.
        admin.MapPost("/imports", async (DestinationRequest request, IMessageBus bus) =>
        {
            await bus.SendAsync(new ImportPlacesCommand(request.Destination));
            return Results.Accepted();
        });
        admin.MapPost("/enrichments", async (DestinationRequest request, IMessageBus bus) =>
        {
            await bus.SendAsync(new EnrichPlacesCommand(request.Destination));
            return Results.Accepted();
        });
        admin.MapPost("/scorings", async (DestinationRequest request, IMessageBus bus) =>
        {
            await bus.SendAsync(new ScorePlacesCommand(request.Destination));
            return Results.Accepted();
        });

        admin.MapGet("/places", (string destination, PlaceStatus? status, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PlaceRecord>>>(new ListPlacesQuery(destination, status, limit ?? 50), ct), places => places.Select(PlaceDto.From)));

        admin.MapGet("/places/{id:guid}", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<PlaceView>>(new GetPlaceQuery(id), ct), view => new { place = PlaceDto.From(view.Place), interests = view.Interests.Select(i => new { code = i.Code, weight = i.Weight }) }));

        admin.MapPost("/places/{id:guid}/publish", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new PublishPlaceCommand(id), ct), version => new { version }));

        admin.MapPost("/places/{id:guid}/unpublish", (Guid id, UnpublishRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new UnpublishPlaceCommand(id, request.Reason), ct), version => new { version }));

        admin.MapPost("/places/{id:guid}/reject", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RejectPlaceCommand(id), ct), _ => new { rejected = true }));

        admin.MapPut("/places/{id:guid}/editorial", (Guid id, EditorialRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetPlaceEditorialCommand(id, request.ImportanceOverride, request.EditoriallySaturated), ct), _ => new { updated = true }));

        admin.MapGet("/dedup", (string destination, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<DedupLink>>>(new ListDedupProposalsQuery(destination), ct), links => links));

        admin.MapPost("/dedup/{id:guid}/confirm", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new ConfirmMergeCommand(id), ct), _ => new { merged = true }));

        admin.MapPost("/dedup/{id:guid}/revert", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RevertMergeCommand(id), ct), _ => new { reverted = true }));

        MapContent(admin);

        // Travelers report a problem with a story (F-20). The only traveler-facing Factory route; the gateway exposes it separately.
        app.MapPost("/api/factory/v1/stories/{id:guid}/reports", (Guid id, ReportRequest request, ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) =>
            user.TravelerId() is { } travelerId
                ? Translate(bus.InvokeAsync<Result<bool>>(new ReportStoryCommand(id, travelerId, request.Reason), ct), _ => new { received = true })
                : Task.FromResult(Results.Unauthorized())).RequireAuthorization(Policies.Traveler);

        return app;
    }

    private static void MapContent(RouteGroupBuilder admin)
    {
        admin.MapPost("/places/{id:guid}/sources", async (Guid id, IMessageBus bus) =>
        {
            await bus.SendAsync(new FetchSourcesCommand(id));
            return Results.Accepted();
        });
        admin.MapPost("/places/{id:guid}/facts/extraction", async (Guid id, IMessageBus bus) =>
        {
            await bus.SendAsync(new ExtractFactsCommand(id));
            return Results.Accepted();
        });
        admin.MapGet("/places/{id:guid}/facts", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<PlaceFacts>>(new ListFactsQuery(id), ct), facts => facts));
        admin.MapPost("/facts/{id:guid}/decision", (Guid id, DecideFactRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new DecideFactCommand(id, request.Accept, request.Reason), ct), _ => new { decided = true }));

        admin.MapPost("/places/{id:guid}/stories", async (Guid id, WriteStoryRequest request, IMessageBus bus) =>
        {
            await bus.SendAsync(new WriteStoryCommand(id, request.Lang, request.Kind));
            return Results.Accepted();
        });
        admin.MapGet("/places/{id:guid}/stories", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<StoryRecord>>>(new ListStoriesQuery(id), ct), stories => stories));
        admin.MapGet("/stories/{id:guid}", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryView>>(new GetStoryQuery(id), ct), view => view));
        admin.MapPut("/stories/{id:guid}/text", (Guid id, EditStoryRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new EditStoryTextCommand(id, request.Title, request.Text), ct), story => story));
        admin.MapPost("/stories/{id:guid}/approve", (Guid id, ApproveStoryRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new ApproveStoryCommand(id, request.EditorialScore), ct), story => story));
        admin.MapPost("/stories/{id:guid}/reject", (Guid id, ReasonRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new RejectStoryCommand(id, request.Reason), ct), story => story));
        admin.MapPost("/stories/{id:guid}/audio", async (Guid id, IMessageBus bus) =>
        {
            await bus.SendAsync(new GenerateAudioCommand(id));
            return Results.Accepted();
        });
        admin.MapPost("/stories/{id:guid}/publish", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new PublishStoryCommand(id), ct), story => story));
        admin.MapPost("/stories/{id:guid}/suspend", (Guid id, ReasonRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new SuspendStoryCommand(id, request.Reason), ct), story => story));
        admin.MapPost("/stories/{id:guid}/resume", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new ResumeStoryCommand(id), ct), story => story));
        admin.MapPost("/stories/{id:guid}/correction", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new OpenCorrectionCommand(id), ct), story => story));
    }

    private static async Task<IResult> Translate<T>(Task<Result<T>> pending, Func<T, object> project)
    {
        var result = await pending;
        if (result.IsSuccess)
        {
            return Results.Ok(project(result.Value!));
        }

        var error = result.Error!;
        var status = error.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound
            : error.Code is "validation" ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;
        return Results.Problem(title: error.Message, statusCode: status, type: $"https://on.voyage/problems/{error.Code}");
    }
}

internal sealed record PlaceDto(
    Guid Id, string Slug, string Name, string? NameEn, double Latitude, double Longitude, string? Qid, string Status,
    int? ImportanceScore, int? PopularityPercentile, bool HiddenGem, string? Classification, long AnnualPageviews, int PublishedVersion,
    string? DescriptionFr, IReadOnlyList<string> HeritageStatuses)
{
    public static PlaceDto From(PlaceRecord place) => new(
        place.Id, place.Slug, place.Name, place.NameEn, place.Location.Latitude, place.Location.Longitude, place.Qid, place.Status.ToString(),
        place.ImportanceScore, place.PopularityPercentile, place.HiddenGem, place.ClassificationOutcome, place.AnnualPageviews, place.PublishedVersion,
        place.Enrichment?.DescriptionFr, place.Enrichment?.HeritageStatuses ?? []);
}

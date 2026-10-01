using System.Security.Claims;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Admin;
using OnVoyage.Factory.Application.Features.Batches;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.Places;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Application.Features.Videos;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Factory.Api.Endpoints;

internal sealed record UnpublishRequest(string Reason);

internal sealed record EditorialRequest(int? ImportanceOverride, bool? EditoriallySaturated);

internal sealed record EthicsRequest(bool Fragile, bool AccessRegulated);

internal sealed record InterestsRequest(Dictionary<string, double> Weights);

internal sealed record DestinationRequest(string Destination);

internal sealed record DecideFactRequest(bool Accept, string? Reason);

internal sealed record WriteStoryRequest(string Lang, StoryKind Kind);

internal sealed record EditStoryRequest(string Title, string Text);

internal sealed record ApproveStoryRequest(double? EditorialScore);

internal sealed record BatchRequest(string Destination, int? MinImportance, string[]? PlaceStatuses, string? Lang, StoryKind? Kind, int? Limit);

internal sealed record ResolveReportsRequest(string Status, string? Note);

internal sealed record SelectVideoRequest(string VideoId);

internal sealed record VoiceRequest(string Voice);

internal sealed record ReasonRequest(string Reason);

internal sealed record ReportRequest(string Reason);

internal sealed record PronunciationRequest(string Replacement);

/// <summary>Every write on the back-office is journaled (SEC-10): who, what, which target, outcome. Reads are not.</summary>
internal sealed class AuditFilter(IMessageBus bus) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var write = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method);
        var summary = write ? AdminActionSummary.Of(context) : null;
        var result = await next(context);
        if (write)
        {
            var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : http.Response.StatusCode;
            var actor = http.User.TravelerId()?.ToString() ?? "unknown";
            await bus.InvokeAsync<Result<bool>>(new RecordAuditCommand(actor, $"{http.Request.Method} {http.GetEndpoint()?.DisplayName}", http.Request.Path.Value ?? string.Empty, status, summary), http.RequestAborted);
        }

        return result;
    }
}

internal static class FactoryEndpoints
{
    public static IEndpointRouteBuilder MapFactoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything in Factory is back-office: no traveler route exists (§9.3).
        var admin = app.MapGroup("/api/factory/v1/admin").RequireAuthorization(Policies.Admin).AddEndpointFilter<AuditFilter>();

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
            Translate(bus.InvokeAsync<Result<PlaceView>>(new GetPlaceQuery(id), ct), view => new { place = PlaceDto.From(view.Place), interests = view.Interests.Select(i => new { code = i.Code, weight = i.Weight }), ethics = new { fragile = view.Detail.Fragile, accessRegulated = view.Detail.AccessRegulated }, crowd = new { offpeak = view.Detail.Offpeak, shoulder = view.Detail.Shoulder, peak = view.Detail.Peak }, importanceOverride = view.Place.ImportanceOverride, saturated = view.Place.EditoriallySaturated }));

        admin.MapPost("/places/{id:guid}/publish", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new PublishPlaceCommand(id), ct), version => new { version }));

        admin.MapPost("/places/{id:guid}/unpublish", (Guid id, UnpublishRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new UnpublishPlaceCommand(id, request.Reason), ct), version => new { version }));

        admin.MapPost("/places/{id:guid}/reject", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RejectPlaceCommand(id), ct), _ => new { rejected = true }));

        admin.MapPut("/places/{id:guid}/editorial", (Guid id, EditorialRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetPlaceEditorialCommand(id, request.ImportanceOverride, request.EditoriallySaturated), ct), _ => new { updated = true }));

        admin.MapPut("/places/{id:guid}/ethics", (Guid id, EthicsRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetPlaceEthicsCommand(id, request.Fragile, request.AccessRegulated), ct), _ => new { updated = true }));

        admin.MapPut("/places/{id:guid}/interests", (Guid id, InterestsRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetPlaceInterestsCommand(id, request.Weights), ct), _ => new { updated = true }));

        admin.MapGet("/dedup", (string destination, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<DedupLink>>>(new ListDedupProposalsQuery(destination), ct), links => links));

        admin.MapPost("/dedup/{id:guid}/confirm", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new ConfirmMergeCommand(id), ct), _ => new { merged = true }));

        admin.MapPost("/dedup/{id:guid}/revert", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RevertMergeCommand(id), ct), _ => new { reverted = true }));

        MapContent(admin);
        MapReferences(admin);
        MapBatches(admin);
        MapVideos(admin);

        // Travelers report a problem with a story (F-20). The only traveler-facing Factory route; the gateway exposes it separately.
        app.MapPost("/api/factory/v1/stories/{id:guid}/reports", (Guid id, ReportRequest request, ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) =>
            user.TravelerId() is { } travelerId
                ? Translate(bus.InvokeAsync<Result<bool>>(new ReportStoryCommand(id, travelerId, request.Reason), ct), _ => new { received = true })
                : Task.FromResult(Results.Unauthorized())).RequireAuthorization(Policies.Traveler);

        return app;
    }

    private static void MapVideos(RouteGroupBuilder admin)
    {
        // The only route that calls YouTube; the apps never do (F-19).
        admin.MapGet("/videos/search", (string q, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<VideoCandidate>>>(new SearchVideosQuery(q), ct), items => items));
        admin.MapGet("/places/{id:guid}/videos", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PlaceVideo>>>(new ListPlaceVideosQuery(id), ct), items => items));
        admin.MapPost("/places/{id:guid}/videos", (Guid id, SelectVideoRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<PlaceVideo>>(new SelectVideoCommand(id, request.VideoId), ct), video => video));
        admin.MapDelete("/places/{id:guid}/videos/{videoId}", (Guid id, string videoId, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RemoveVideoCommand(id, videoId), ct), _ => new { removed = true }));
    }

    private static void MapBatches(RouteGroupBuilder admin)
    {
        admin.MapPost("/batches", async (BatchRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
        {
            var statuses = (request.PlaceStatuses is { Length: > 0 } names ? names : ["Candidate", "Published"]).Select(name => Enum.TryParse<PlaceStatus>(name, true, out var parsed) ? (PlaceStatus?)parsed : null).ToList();
            if (statuses.Any(status => status is null))
            {
                return Results.Problem(title: "Unknown place status.", statusCode: StatusCodes.Status400BadRequest, type: "https://on.voyage/problems/validation");
            }

            var criteria = new BatchCriteria(request.Destination, request.MinImportance, [.. statuses.Select(status => status!.Value)], request.Lang ?? "fr", request.Kind ?? StoryKind.Standard, request.Limit ?? 20);
            var result = await bus.InvokeAsync<Result<GenerationBatch>>(new CreateBatchCommand(criteria, http.User.TravelerId()?.ToString() ?? "unknown"), ct);
            return result.IsSuccess
                ? Results.Accepted($"/api/factory/v1/admin/batches/{result.Value!.Id}", new { id = result.Value.Id, total = result.Value.Total })
                : Problem(result.Error!);
        });

        admin.MapGet("/batches", (int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<BatchProgress>>>(new ListBatchesQuery(limit ?? 20), ct), items => items));

        admin.MapGet("/batches/{id:guid}", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<BatchDetail>>(new GetBatchQuery(id), ct), detail => detail));

        admin.MapPost("/batches/{id:guid}/retry", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new RetryFailedJobsCommand(id), ct), count => new { requeued = count }));
    }

    private static void MapReferences(RouteGroupBuilder admin)
    {
        admin.MapGet("/destinations", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<DestinationConfig>>>(new ListDestinationsQuery(), ct), items => items.Select(item => new { slug = item.Slug, name = item.Name, latitude = item.Center.Latitude, longitude = item.Center.Longitude })));

        admin.MapGet("/audit", (int? limit, string? actor, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<AuditEntry>>>(new ListAuditQuery(limit ?? 100, actor), ct), items => items));

        admin.MapGet("/stories", (ContentStatus? status, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<StoryRecord>>>(new ListStoriesByStatusQuery(status ?? ContentStatus.NeedsReview, limit ?? 50), ct), items => items));

        admin.MapGet("/pronunciations", (string destination, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PronunciationEntry>>>(new ListPronunciationsQuery(destination), ct), items => items));

        admin.MapPut("/pronunciations/{destination}/{term}", (string destination, string term, PronunciationRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetPronunciationCommand(destination, term, request.Replacement), ct), _ => new { saved = true }));

        admin.MapDelete("/pronunciations/{destination}/{term}", (string destination, string term, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new DeletePronunciationCommand(destination, term), ct), _ => new { deleted = true }));
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
        admin.MapGet("/reports", (string? status, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<ReportInboxItem>>>(new ListReportInboxQuery(status, limit ?? 100), ct), items => items));
        admin.MapPost("/stories/{id:guid}/reports/resolve", (Guid id, ResolveReportsRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<int>>(new ResolveStoryReportsCommand(id, request.Status, request.Note), ct), closed => new { closed }));
        admin.MapPut("/stories/{id:guid}/voice", (Guid id, VoiceRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new ChangeStoryVoiceCommand(id, request.Voice), ct), story => story));
        admin.MapPost("/stories/{id:guid}/audio/reset", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StoryRecord>>(new ResetAudioCommand(id), ct), story => story));
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

        return Problem(result.Error!);
    }

    private static IResult Problem(Error error)
    {
        var status = error.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound
            : error.Code is "validation" ? StatusCodes.Status400BadRequest
            : error.Code is "youtube_not_configured" ? StatusCodes.Status503ServiceUnavailable
            : error.Code is "youtube_unavailable" ? StatusCodes.Status502BadGateway
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

using OnVoyage.Creators.Api.Endpoints.Studio;
using OnVoyage.Creators.Application;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using static OnVoyage.Creators.Api.Endpoints.Responses;

namespace OnVoyage.Creators.Api.Endpoints;

internal static class CreatorsEndpoints
{
    public static IEndpointRouteBuilder MapCreatorsEndpoints(this IEndpointRouteBuilder app)
    {
        // Travelers (anonymous sessions included). No route takes a position (F-30): the block of a place is asked by its identifier.
        var travelers = app.MapGroup("/api/creators/v1").RequireAuthorization(Policies.Traveler);

        // Public reads: a traveler's session, or an internal host (Web.Public renders the creator pages with an internal token: no follower, no position).
        var reads = app.MapGroup("/api/creators/v1").RequireAuthorization(Policies.TravelerOrInternal);

        reads.MapGet("/creators/{handle}", (string handle, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CreatorPageDto>>(new GetCreatorPageQuery(handle, http.User.TravelerId()), ct)));

        reads.MapGet("/creators", (string? destination, string? specialty, string? cursor, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CreatorListDto>>(new ListCreatorsQuery(destination, specialty, cursor, limit), ct)));

        reads.MapGet("/pois/{poiId:guid}/contents", (Guid poiId, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<PoiCreatorsDto>>(new GetPoiCreatorsQuery(poiId, limit), ct)));

        travelers.MapPut("/me/follows/{creatorId:guid}", (Guid creatorId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<FollowStateDto>>(new SetFollowCommand(Traveler(http), creatorId, true), ct)));

        travelers.MapDelete("/me/follows/{creatorId:guid}", (Guid creatorId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<FollowStateDto>>(new SetFollowCommand(Traveler(http), creatorId, false), ct)));

        travelers.MapGet("/me/follows", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<FollowedCreatorDto>>>(new ListFollowsQuery(Traveler(http)), ct)));

        travelers.MapPost("/reports", (ReportRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ReportReceiptDto>>(new ReportCommand(Traveler(http), request), ct), receipt => Results.Accepted(value: receipt)));

        MapAdmin(app);
        app.MapStudioEndpoints();
        return app;
    }

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        // Back-office (SEC-03: the service refuses a token without the admin role itself). Every write is journaled with its change.
        var admin = app.MapGroup("/api/creators/v1/admin").RequireAuthorization(Policies.Admin);

        admin.MapGet("/creators", (string? status, string? search, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<AdminCreatorSummaryDto>>>(new ListCreatorsAdminQuery(status, search, limit ?? 100), ct)));

        admin.MapPost("/creators", (CreatorProfileRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new CreateFounderCommand(Actor(http), request), ct), created => Results.Created($"/api/creators/v1/admin/creators/{created.Id}", created)));

        admin.MapGet("/creators/{id:guid}", (Guid id, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new GetCreatorAdminQuery(id), ct)));

        admin.MapPut("/creators/{id:guid}", (Guid id, CreatorProfileRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new UpdateCreatorCommand(Actor(http), id, request), ct)));

        admin.MapPut("/creators/{id:guid}/consent", (Guid id, FounderConsentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new RecordFounderConsentCommand(Actor(http), id, request), ct)));

        admin.MapPut("/creators/{id:guid}/account", (Guid id, LinkAccountRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new LinkAccountCommand(Actor(http), id, request.AccountId), ct)));

        admin.MapPost("/creators/{id:guid}/publish", (Guid id, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new PublishCreatorCommand(Actor(http), id), ct)));

        admin.MapPost("/creators/{id:guid}/unpublish", (Guid id, ReasonRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new UnpublishCreatorCommand(Actor(http), id, request.Reason), ct)));

        admin.MapPost("/creators/{id:guid}/suspend", (Guid id, ReasonRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new SuspendCreatorCommand(Actor(http), id, request.Reason), ct)));

        admin.MapPost("/creators/{id:guid}/handle-claim", (Guid id, ClaimHandleRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminCreatorDetailDto>>(new ClaimHandleCommand(Actor(http), id, request.Handle, request.Reason), ct)));

        admin.MapPost("/creators/{id:guid}/contents", (Guid id, AddContentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminContentDto>>(new AddContentCommand(Actor(http), id, request), ct), created => Results.Created($"/api/creators/v1/admin/creators/{id}", created)));

        admin.MapPut("/creators/{id:guid}/contents/{contentId:guid}", (Guid id, Guid contentId, UpdateContentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminContentDto>>(new UpdateContentCommand(Actor(http), id, contentId, request), ct)));

        admin.MapDelete("/creators/{id:guid}/contents/{contentId:guid}", (Guid id, Guid contentId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RemoveContentCommand(Actor(http), id, contentId), ct), _ => Results.NoContent()));

        admin.MapPost("/creators/{id:guid}/place-links", (Guid id, AddPlaceLinkRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminPlaceLinkDto>>(new AddPlaceLinkCommand(Actor(http), id, request), ct)));

        admin.MapPut("/creators/{id:guid}/place-links/{linkId:guid}", (Guid id, Guid linkId, SetPlaceLinkStatusRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminPlaceLinkDto>>(new SetPlaceLinkStatusCommand(Actor(http), id, linkId, request.Status), ct)));

        admin.MapDelete("/creators/{id:guid}/place-links/{linkId:guid}", (Guid id, Guid linkId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RemovePlaceLinkCommand(Actor(http), id, linkId), ct), _ => Results.NoContent()));

        admin.MapPut("/creators/{id:guid}/tips/{poiId:guid}", (Guid id, Guid poiId, SetTipRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminTipDto>>(new SetTipCommand(Actor(http), id, poiId, request.Text), ct)));

        admin.MapDelete("/creators/{id:guid}/tips/{poiId:guid}", (Guid id, Guid poiId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new RemoveTipCommand(Actor(http), id, poiId), ct), _ => Results.NoContent()));

        admin.MapGet("/places", (string? query, string? destination, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PoiSearchResultDto>>>(new SearchPlacesQuery(query ?? string.Empty, destination, limit ?? 20), ct)));

        admin.MapGet("/moderation", (string? status, int? limit, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<ModerationCaseDto>>>(new ListModerationQuery(status, limit ?? 100), ct)));

        admin.MapPost("/moderation/{caseId:guid}/decision", (Guid caseId, DecideCaseRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ModerationCaseDto>>(new DecideCaseCommand(Actor(http), caseId, request.Decision, request.StatementOfReasons), ct)));
    }

    private static Guid Traveler(HttpContext http) => http.User.TravelerId() ?? throw new InvalidOperationException("Authenticated request without a traveler id.");

    private static Guid Actor(HttpContext http) => Traveler(http);
}

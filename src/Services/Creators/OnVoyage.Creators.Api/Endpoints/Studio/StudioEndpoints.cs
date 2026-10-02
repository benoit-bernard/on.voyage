using OnVoyage.Creators.Application;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using static OnVoyage.Creators.Api.Endpoints.Responses;

namespace OnVoyage.Creators.Api.Endpoints.Studio;

/// <summary>
/// The creator's own space (F-26, T-1206). Sign-up and the registration state need a verified e-mail (policy <c>account</c>): the creator role does not
/// exist yet. Everything else needs the <c>creator</c> role (SEC-03: the service refuses a token without it itself, the Gateway is only the first gate).
/// The creator is always the account of the token.
/// </summary>
internal static class StudioEndpoints
{
    public static IEndpointRouteBuilder MapStudioEndpoints(this IEndpointRouteBuilder app)
    {
        var join = app.MapGroup("/api/creators/v1/studio").RequireAuthorization(Policies.Account);

        join.MapGet("/registration", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioRegistrationDto>>(new GetRegistrationQuery(Account(http)), ct)));

        join.MapPost("/signup", (StudioSignupRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioRegistrationDto>>(new StudioSignupCommand(Account(http), request), ct)));

        join.MapPost("/terms", (AcceptTermsRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioRegistrationDto>>(new AcceptTermsCommand(Account(http), request.AcceptedTermsVersion), ct)));

        var studio = app.MapGroup("/api/creators/v1/studio").RequireAuthorization(Policies.Creator);

        studio.MapGet("/profile", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioProfileDto>>(new GetStudioProfileQuery(Account(http)), ct)));

        studio.MapPut("/profile", (CreatorProfileRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioProfileDto>>(new UpdateStudioProfileCommand(Account(http), request), ct)));

        studio.MapPost("/publish", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioProfileDto>>(new PublishStudioCommand(Account(http)), ct)));

        studio.MapPost("/unpublish", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<StudioProfileDto>>(new UnpublishStudioCommand(Account(http)), ct)));

        studio.MapPost("/contents", (AddContentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminContentDto>>(new StudioAddContentCommand(Account(http), request), ct), created => Results.Created("/api/creators/v1/studio/profile", created)));

        studio.MapPut("/contents/{contentId:guid}", (Guid contentId, UpdateContentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminContentDto>>(new StudioUpdateContentCommand(Account(http), contentId, request), ct)));

        studio.MapDelete("/contents/{contentId:guid}", (Guid contentId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new StudioRemoveContentCommand(Account(http), contentId), ct), _ => Results.NoContent()));

        studio.MapPost("/place-links", (AddPlaceLinkRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminPlaceLinkDto>>(new StudioAddPlaceLinkCommand(Account(http), request), ct)));

        studio.MapPut("/place-links/{linkId:guid}", (Guid linkId, SetPlaceLinkStatusRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminPlaceLinkDto>>(new StudioSetPlaceLinkStatusCommand(Account(http), linkId, request.Status), ct)));

        studio.MapDelete("/place-links/{linkId:guid}", (Guid linkId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new StudioRemovePlaceLinkCommand(Account(http), linkId), ct), _ => Results.NoContent()));

        studio.MapPut("/tips/{poiId:guid}", (Guid poiId, SetTipRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminTipDto>>(new StudioSetTipCommand(Account(http), poiId, request.Text), ct)));

        studio.MapDelete("/tips/{poiId:guid}", (Guid poiId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new StudioRemoveTipCommand(Account(http), poiId), ct), _ => Results.NoContent()));

        studio.MapGet("/places", (string? query, string? destination, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<PoiSearchResultDto>>>(new StudioSearchPlacesQuery(query ?? string.Empty, destination), ct)));

        return app;
    }

    private static Guid Account(HttpContext http) => http.User.TravelerId() ?? throw new InvalidOperationException("Authenticated request without an account id.");
}

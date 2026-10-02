using OnVoyage.Discovery.Application;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Discovery.Api.Endpoints;

internal static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discovery/v1").RequireAuthorization(Policies.Traveler);

        group.MapGet("/onboarding/clips", (string? lang, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<OnboardingClipDto>>>(new GetOnboardingClipsQuery(lang ?? "fr"), ct)));

        group.MapPost("/onboarding", (OnboardingRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<InteractionBatchResponse>>(new SubmitOnboardingCommand(Traveler(http), request), ct)));

        group.MapGet("/me/profile", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ProfileDto>>(new GetProfileQuery(Traveler(http)), ct)));

        group.MapPatch("/me/profile", (ProfileCorrectionRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ProfileDto>>(new CorrectProfileCommand(Traveler(http), request.Corrections), ct)));

        group.MapGet("/me/settings", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<SettingsDto>>(new GetSettingsQuery(Traveler(http)), ct)));

        group.MapPatch("/me/settings", (SettingsPatch patch, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<SettingsDto>>(new UpdateSettingsCommand(Traveler(http), patch), ct)));

        group.MapPost("/me/interactions", (InteractionBatchRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<InteractionBatchResponse>>(new IngestInteractionsCommand(Traveler(http), request.Interactions), ct)));

        group.MapGet("/recommendations", (double? lat, double? lng, int? radius, int? limit, string? context, string? surface, string? destination, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<RecommendationsDto>>(
                new GetRecommendationsQuery(Traveler(http), lat, lng, radius ?? 5_000, limit ?? 10, context, surface, destination ?? "marseille"), ct)));

        group.MapGet("/destinations/{slug}/for-me", (string slug, double? lat, double? lng, int? days, string? mobility, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<DestinationForMeDto>>(new GetDestinationForMeQuery(Traveler(http), slug, lat, lng, days, mobility), ct)));

        group.MapGet("/surprise", (double? lat, double? lng, int? radius, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<RecommendationItemDto>>(new GetSurpriseQuery(Traveler(http), lat, lng, radius ?? 5_000), ct)));

        group.MapGet("/me/candidates", (string? destination, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CandidatesDto>>(new GetCandidatesQuery(Traveler(http), destination ?? "marseille", string.Empty), ct)));

        group.MapGet("/me/cf-scores", (string? destination, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CfScoresDto>>(new GetCfScoresQuery(Traveler(http), destination ?? "marseille"), ct)));

        group.MapGet("/creators/for-me", (string? destination, int? limit, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CreatorsForMeDto>>(new GetCreatorsForMeQuery(Traveler(http), destination ?? "marseille", limit ?? 20), ct)));

        group.MapGet("/me/saved", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<SavedGroupDto>>>(new GetSavedQuery(Traveler(http)), ct)));

        group.MapPut("/me/saved/{poiId:guid}", (Guid poiId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetSavedCommand(Traveler(http), poiId, true), ct)));

        group.MapDelete("/me/saved/{poiId:guid}", (Guid poiId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetSavedCommand(Traveler(http), poiId, false), ct)));

        group.MapGet("/me/history", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<HistoryItemDto>>>(new GetHistoryQuery(Traveler(http)), ct)));

        group.MapDelete("/me/history/{poiId:guid}", (Guid poiId, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<InteractionBatchResponse>>(new DeleteHistoryCommand(Traveler(http), poiId), ct)));

        var admin = app.MapGroup("/api/discovery/v1/admin").RequireAuthorization(Policies.Admin);

        admin.MapGet("/onboarding-clips", (string? lang, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminClipsDto>>(new GetAdminClipsQuery(lang ?? "fr"), ct)));

        admin.MapPut("/onboarding-clips", (ActiveClipsRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetActiveClipsCommand(request.StoryIds), ct)));

        // The six-hourly job runs this on its own; the endpoint lets an operator rerun it (after an import, or to check a threshold).
        admin.MapPost("/cf-scores/recompute", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<CfRunSummary>>(new RecomputeCfScoresCommand(), ct)));

        return app;
    }

    private static Guid Traveler(HttpContext http) => http.User.TravelerId() ?? throw new InvalidOperationException("Authenticated request without a traveler id.");

    private static async Task<IResult> Translate<T>(Task<Result<T>> pending)
    {
        var result = await pending;
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        var error = result.Error!;
        var status = error.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        return Results.Problem(title: error.Message, statusCode: status, type: $"https://on.voyage/problems/{error.Code}");
    }
}

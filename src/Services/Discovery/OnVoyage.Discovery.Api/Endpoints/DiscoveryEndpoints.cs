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

        group.MapPost("/me/interactions", (InteractionBatchRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<InteractionBatchResponse>>(new IngestInteractionsCommand(Traveler(http), request.Interactions), ct)));

        var admin = app.MapGroup("/api/discovery/v1/admin").RequireAuthorization(Policies.Admin);

        admin.MapGet("/onboarding-clips", (string? lang, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AdminClipsDto>>(new GetAdminClipsQuery(lang ?? "fr"), ct)));

        admin.MapPut("/onboarding-clips", (ActiveClipsRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<bool>>(new SetActiveClipsCommand(request.StoryIds), ct)));

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

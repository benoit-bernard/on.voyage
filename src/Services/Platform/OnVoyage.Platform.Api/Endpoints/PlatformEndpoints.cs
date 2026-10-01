using System.Text.Json;
using OnVoyage.Platform.Api.Security;
using OnVoyage.Platform.Application;
using OnVoyage.Platform.Application.Features.Config;
using OnVoyage.Platform.Application.Features.Consents;
using OnVoyage.Platform.Application.Features.Flags;
using OnVoyage.Platform.Contracts;
using Wolverine;

namespace OnVoyage.Platform.Api.Endpoints;

internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/v1");

        group.MapGet("/config", (HttpContext http, string? platform, string? appVersion, string? scope, IMessageBus bus, CancellationToken ct) =>
        {
            var edge = string.Equals(scope, "edge", StringComparison.OrdinalIgnoreCase);
            if (edge && !Access.IsAdmin(http))
            {
                return Task.FromResult(Results.Problem(title: "Internal access required.", statusCode: StatusCodes.Status401Unauthorized, type: "https://on.voyage/problems/unauthorized"));
            }

            return Translate(bus.InvokeAsync<Result<ClientConfigDto>>(
                new GetClientConfigQuery(platform, appVersion, Access.Traveler(http), edge ? ConfigScope.Edge : ConfigScope.Client), ct));
        });

        var admin = group.MapGroup("/admin").AddEndpointFilter<AdminOnly>();

        admin.MapGet("/config/{key}", (string key, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ConfigEntryDto>>(new GetConfigEntryQuery(key), ct)));

        admin.MapPut("/config/{key}", (string key, SetConfigRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ConfigEntryDto>>(new SetConfigCommand(key, request.Value.GetRawText(), "admin"), ct)));

        admin.MapGet("/flags", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<FeatureFlagDto>>>(new ListFlagsQuery(), ct)));

        admin.MapPut("/flags/{name}", (string name, SetFeatureFlagRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<FeatureFlagDto>>(
                new SetFlagCommand(name, request.Enabled, request.RolloutPercent, request.Platforms ?? [], request.MinAppVersion), ct)));

        var me = group.MapGroup("/me");

        me.MapGet("/consents", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Access.Traveler(http) is { } traveler
                ? Translate(bus.InvokeAsync<Result<IReadOnlyList<ConsentDto>>>(new GetConsentsQuery(traveler), ct))
                : Task.FromResult(Unauthorized()));

        me.MapPut("/consents/{kind}", (string kind, SetConsentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Access.Traveler(http) is { } traveler
                ? Translate(bus.InvokeAsync<Result<ConsentDto>>(new SetConsentCommand(traveler, kind, request.Granted, request.TextVersion), ct))
                : Task.FromResult(Unauthorized()));

        return app;
    }

    private static IResult Unauthorized() =>
        Results.Problem(title: "Authentication required.", statusCode: StatusCodes.Status401Unauthorized, type: "https://on.voyage/problems/unauthorized");

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

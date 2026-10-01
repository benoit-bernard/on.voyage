using OnVoyage.Platform.Application;
using OnVoyage.Platform.Application.Features.Auth;
using OnVoyage.Platform.Application.Features.Config;
using OnVoyage.Platform.Application.Features.Consents;
using OnVoyage.Platform.Application.Features.Flags;
using OnVoyage.Platform.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Platform.Api.Endpoints;

internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/v1");

        // Anonymous: apps read the configuration before they have a session. A token, when present, only picks the rollout bucket.
        group.MapGet("/config", (HttpContext http, string? platform, string? appVersion, string? scope, IMessageBus bus, CancellationToken ct) =>
        {
            var edge = string.Equals(scope, "edge", StringComparison.OrdinalIgnoreCase);
            if (edge && !http.User.HasRole("internal"))
            {
                return Task.FromResult(Problem("unauthorized", "Internal access required.", StatusCodes.Status403Forbidden));
            }

            return Translate(bus.InvokeAsync<Result<ClientConfigDto>>(
                new GetClientConfigQuery(platform, appVersion, http.User.TravelerId(), edge ? ConfigScope.Edge : ConfigScope.Client), ct));
        }).AllowAnonymous();

        MapAuth(group);

        var admin = group.MapGroup("/admin").RequireAuthorization(Policies.Admin);

        admin.MapGet("/config/{key}", (string key, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ConfigEntryDto>>(new GetConfigEntryQuery(key), ct)));

        admin.MapPut("/config/{key}", (string key, SetConfigRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ConfigEntryDto>>(new SetConfigCommand(key, request.Value.GetRawText(), $"admin:{http.User.TravelerId()}"), ct)));

        admin.MapGet("/flags", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<FeatureFlagDto>>>(new ListFlagsQuery(), ct)));

        admin.MapPut("/flags/{name}", (string name, SetFeatureFlagRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<FeatureFlagDto>>(
                new SetFlagCommand(name, request.Enabled, request.RolloutPercent, request.Platforms ?? [], request.MinAppVersion), ct)));

        var me = group.MapGroup("/me").RequireAuthorization(Policies.Traveler);

        me.MapGet("", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AccountDto>>(new GetAccountQuery(http.User.TravelerId()!.Value), ct)));

        me.MapGet("/consents", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<ConsentDto>>>(new GetConsentsQuery(http.User.TravelerId()!.Value), ct)));

        me.MapPut("/consents/{kind}", (string kind, SetConsentRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ConsentDto>>(new SetConsentCommand(http.User.TravelerId()!.Value, kind, request.Granted, request.TextVersion), ct)));

        return app;
    }

    private static void MapAuth(RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth").AllowAnonymous();

        auth.MapPost("/anonymous", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AuthSessionDto>>(new StartAnonymousSessionCommand(), ct)));

        auth.MapPost("/refresh", (RefreshSessionRequest request, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<AuthSessionDto>>(new RefreshSessionCommand(request.RefreshToken), ct)));

        auth.MapPost("/signout", async (RefreshSessionRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Result<bool>>(new SignOutCommand(request.RefreshToken), ct);
            return Results.NoContent();
        });

        // A bearer token is optional: it identifies the anonymous account the e-mail will be linked to.
        auth.MapPost("/otp/request", async (RequestOtpRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<bool>>(new RequestOtpCommand(request.Email), ct);
            return result.IsSuccess ? Results.Accepted() : Failure(http, result.Error!);
        });

        auth.MapPost("/otp/verify", (VerifyOtpRequest request, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(http, bus.InvokeAsync<Result<AuthSessionDto>>(new VerifyOtpCommand(http.User.TravelerId(), request.Email, request.Code), ct)));
    }

    private static IResult Problem(string code, string title, int status) =>
        Results.Problem(title: title, statusCode: status, type: $"https://on.voyage/problems/{code}");

    private static Task<IResult> Translate<T>(Task<Result<T>> pending) => Translate(null, pending);

    private static async Task<IResult> Translate<T>(HttpContext? http, Task<Result<T>> pending)
    {
        var result = await pending;
        return result.IsSuccess ? Results.Ok(result.Value) : Failure(http, result.Error!);
    }

    private static IResult Failure(HttpContext? http, Error error)
    {
        var status = error.Code switch
        {
            "otp_cooldown" or "otp_rate_limited" => StatusCodes.Status429TooManyRequests,
            "invalid_refresh_token" => StatusCodes.Status401Unauthorized,
            "email_already_linked" => StatusCodes.Status409Conflict,
            "email_unavailable" => StatusCodes.Status502BadGateway,
            _ when error.Code.EndsWith("not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };

        if (error.RetryAfterSeconds is { } seconds && http is not null)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Problem(error.Code, error.Message, status);
    }
}

using OnVoyage.Platform.Application.Features.DataRights;
using OnVoyage.Platform.Application;
using OnVoyage.Platform.Application.Features.Audit;
using OnVoyage.Platform.Application.Features.Auth;
using OnVoyage.Platform.Application.Features.Config;
using OnVoyage.Platform.Application.Features.Consents;
using OnVoyage.Platform.Application.Features.Flags;
using OnVoyage.Platform.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Platform.Api.Endpoints;

/// <summary>Every admin write in Platform is journaled (SEC-10): who, what, which target, outcome, and what was asked.</summary>
internal sealed class AdminAuditFilter(IMessageBus bus) : IEndpointFilter
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
            await bus.InvokeAsync<Result<bool>>(
                new RecordPlatformAdminActionCommand(http.User.TravelerId()?.ToString() ?? "unknown", $"{http.Request.Method} {http.GetEndpoint()?.DisplayName}", http.Request.Path.Value ?? string.Empty, status, summary), http.RequestAborted);
        }

        return result;
    }
}

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

        var admin = group.MapGroup("/admin").RequireAuthorization(Policies.Admin).AddEndpointFilter<AdminAuditFilter>();

        admin.MapGet("/audit", (int? limit, string? service, string? actor, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<AdminActionDto>>>(new ListAdminAuditQuery(limit ?? 100, service, actor), ct)));

        admin.MapGet("/config", (IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<ConfigEntryDto>>>(new ListConfigQuery(), ct)));

        admin.MapGet("/config/{key}/history", (string key, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<IReadOnlyList<ConfigEntryDto>>>(new GetConfigHistoryQuery(key), ct)));

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

        // Data rights (F-22, T-507): a copy of one's data, and being forgotten.
        me.MapPost("/export", async (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            await Translate(bus.InvokeAsync<Result<ExportStatusDto>>(new RequestExportCommand(http.User.TravelerId()!.Value), ct), StatusCodes.Status202Accepted));

        me.MapGet("/export/{id:guid}", (Guid id, HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<ExportStatusDto>>(new GetExportQuery(http.User.TravelerId()!.Value, id), ct)));

        me.MapGet("/export/{id:guid}/archive", async (Guid id, HttpContext http, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<string>>(new GetExportArchiveQuery(http.User.TravelerId()!.Value, id), ct);
            if (!result.IsSuccess)
            {
                return Problem(result.Error!);
            }

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"on-voyage-mes-donnees.json\"";
            return Results.Text(result.Value!, "application/json");
        });

        me.MapPost("/deletion", async (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            await Translate(bus.InvokeAsync<Result<DeletionStatusDto>>(new RequestDeletionCommand(http.User.TravelerId()!.Value), ct), StatusCodes.Status202Accepted));

        me.MapGet("/deletion", (HttpContext http, IMessageBus bus, CancellationToken ct) =>
            Translate(bus.InvokeAsync<Result<DeletionStatusDto>>(new GetDeletionQuery(http.User.TravelerId()!.Value), ct)));

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

    /// <summary>For requests that start work (export, deletion): 202 with the status instead of 200.</summary>
    private static async Task<IResult> Translate<T>(Task<Result<T>> pending, int successStatus)
    {
        var result = await pending;
        return result.IsSuccess ? Results.Json(result.Value, statusCode: successStatus) : Failure(null, result.Error!);
    }

    private static IResult Problem(Error error) => Failure(null, error);

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
            "export_not_ready" => StatusCodes.Status409Conflict,
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

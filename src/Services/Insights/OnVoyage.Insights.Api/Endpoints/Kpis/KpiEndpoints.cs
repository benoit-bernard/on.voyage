using OnVoyage.Insights.Api.Endpoints.Events;
using OnVoyage.Insights.Application;
using OnVoyage.Insights.Application.Features.Kpis;
using OnVoyage.Insights.Contracts;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;

namespace OnVoyage.Insights.Api.Endpoints.Kpis;

internal static class KpiEndpoints
{
    public static IEndpointRouteBuilder MapKpiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/insights/v1/kpis").RequireAuthorization(Policies.Admin);

        // `to` defaults to today and `from` to 30 days before it, so a bare call answers something useful.
        group.MapGet("/", async (DateOnly? from, DateOnly? to, string? destination, string? cohort, TimeProvider clock, IMessageBus bus, CancellationToken ct) =>
        {
            var (start, end) = Period(from, to, clock);
            return Problems.Translate(await bus.InvokeAsync<Result<KpiReportDto>>(new GetKpisQuery(start, end, destination, cohort), ct));
        });

        group.MapGet("/export", async (DateOnly? from, DateOnly? to, string? destination, string? cohort, TimeProvider clock, IMessageBus bus, CancellationToken ct) =>
        {
            var (start, end) = Period(from, to, clock);
            var result = await bus.InvokeAsync<Result<string>>(new ExportKpisQuery(start, end, destination, cohort), ct);
            return result.IsSuccess
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(result.Value!), "text/csv; charset=utf-8", $"kpis-{start:yyyyMMdd}-{end:yyyyMMdd}.csv")
                : Problems.Translate(result);
        });

        return app;
    }

    private static (DateOnly From, DateOnly To) Period(DateOnly? from, DateOnly? to, TimeProvider clock)
    {
        var end = to ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return (from ?? end.AddDays(-30), end);
    }
}

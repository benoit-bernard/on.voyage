using OnVoyage.Insights.Application.Features.Kpis;
using OnVoyage.Insights.Application.Features.Maintenance;
using Wolverine;

namespace OnVoyage.Insights.Api.Jobs;

/// <summary>
/// Runs the two recurring jobs of the service: the housekeeping of the event table (partitions, retention) every day and the rebuild of the
/// recent daily KPIs every hour. Both are idempotent, so several instances running them together is harmless. Turned off with
/// <c>Insights:Jobs:Enabled=false</c> (tests call the commands directly).
/// </summary>
internal sealed class InsightsScheduler(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<InsightsScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Insights:Jobs:Enabled", true))
        {
            return;
        }

        var maintenanceEvery = TimeSpan.FromHours(configuration.GetValue("Insights:Jobs:MaintenanceHours", 24));
        var kpiEvery = TimeSpan.FromMinutes(configuration.GetValue("Insights:Jobs:KpiMinutes", 60));
        await Task.WhenAll(Loop("maintenance", maintenanceEvery, bus => bus.InvokeAsync(new RunMaintenanceCommand(), stoppingToken), stoppingToken),
            Loop("kpi-refresh", kpiEvery, bus => bus.InvokeAsync(new RefreshRecentKpisCommand(), stoppingToken), stoppingToken));
    }

    private async Task Loop(string name, TimeSpan every, Func<IMessageBus, Task> run, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await run(scope.ServiceProvider.GetRequiredService<IMessageBus>());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Insights job {Job} failed; it will run again at the next tick.", name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

using System.Text.Json;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Features.Bootstrap;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.Snapshot;
using OnVoyage.Factory.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using Wolverine;

// Command line: `dotnet run --project <this> -- bootstrap marseille --Bootstrap:MaxPlaces=40 --Bootstrap:BudgetUsd=5 ...` or `-- snapshot marseille`.
// Without a verb the process is the long-running worker. The verbs run one command in this process (same handlers as the queue), print a JSON
// report and exit with 0 when it succeeded; docs/runbooks/bootstrap-marseille.md describes the options.
string? verb = args.Length >= 2 && args[0] is "bootstrap" or "snapshot" ? args[0] : null;
var verbDestination = verb is null ? null : args[1];
var hostArguments = verb is null ? args : args[2..];

var builder = WebApplication.CreateBuilder(hostArguments);
if (verb is not null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:0"); // the health endpoint is not needed: take any free port
    builder.Configuration["Factory:Snapshot:ImportOnStart"] = "false";
}

builder.AddServiceDefaults();
builder.Services.AddFactoryInfrastructure(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(ImportPlacesCommand).Assembly);

    // The worker listens on the "factory" queue and runs the pipeline jobs; a job's follow-up (import, then enrichment, then scoring)
    // is queued again so each step is retried on its own.
    options.AddOnVoyageMessaging(connectionString, "factory", configureFailures: MessagingRoutes.RetryProviderFailures(builder.Configuration), maxParallelMessages: builder.Configuration.GetValue("Factory:Jobs:MaxParallel", 4));
    options.RouteFactoryJobs();
    options.RouteFactoryEvents();
});
builder.Services.AddFactoryInitializer();
builder.Services.AddFactorySnapshotOnStart();

var app = builder.Build();
app.MapDefaultEndpoints();

if (verb is null)
{
    app.Run();
    return 0;
}

await app.StartAsync();
int exitCode;
try
{
    var bus = app.Services.GetRequiredService<IMessageBus>();
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    if (verb == "snapshot")
    {
        var result = await bus.InvokeAsync<Result<SnapshotImportSummary>>(new ImportSnapshotCommand(verbDestination!));
        Console.WriteLine(result.IsSuccess ? JsonSerializer.Serialize(result.Value, options) : $"{result.Error!.Code}: {result.Error.Message}");
        exitCode = result.IsSuccess && result.Value!.Failed == 0 ? 0 : 1;
    }
    else
    {
        var section = app.Configuration.GetSection("Bootstrap");
        var defaults = new BootstrapDestinationCommand(verbDestination!);
        var command = defaults with
        {
            MaxPlaces = section.GetValue("MaxPlaces", defaults.MaxPlaces),
            MinImportance = section.GetValue<int?>("MinImportance"),
            Lang = section.GetValue("Lang", defaults.Lang)!,
            BudgetUsd = section.GetValue("BudgetUsd", defaults.BudgetUsd),
            AutoPublish = section.GetValue("AutoPublish", defaults.AutoPublish),
            ForceImport = section.GetValue("ForceImport", defaults.ForceImport),
            SkipImport = section.GetValue("SkipImport", defaults.SkipImport),
            AllowUnpriced = section.GetValue("AllowUnpriced", defaults.AllowUnpriced),
            PauseMilliseconds = section.GetValue("PauseMilliseconds", defaults.PauseMilliseconds),
            RetryDelaysSeconds = section.GetSection("RetryDelaysSeconds").Get<double[]>(),
        };
        var result = await bus.InvokeAsync<Result<BootstrapReport>>(command);
        Console.WriteLine(result.IsSuccess ? JsonSerializer.Serialize(result.Value, options) : $"{result.Error!.Code}: {result.Error.Message}");
        exitCode = result.IsSuccess && result.Value!.Outcome == BootstrapDestinationHandler.Completed ? 0 : 1;
    }
}
finally
{
    await app.StopAsync();
}

return exitCode;

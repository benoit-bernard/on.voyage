using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddFactoryInfrastructure(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(ImportPlacesCommand).Assembly);

    // The worker listens on the "factory" queue and runs the pipeline jobs; a job's follow-up (import, then enrichment, then scoring)
    // is queued again so each step is retried on its own.
    options.AddOnVoyageMessaging(connectionString, "factory", configureFailures: MessagingRoutes.RetryProviderFailures);
    options.RouteFactoryJobs();
    options.RouteFactoryEvents();
});
builder.Services.AddFactoryInitializer();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

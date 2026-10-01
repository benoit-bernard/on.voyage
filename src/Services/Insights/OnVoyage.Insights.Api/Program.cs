using FluentValidation;
using OnVoyage.Insights.Api.Endpoints.Events;
using OnVoyage.Insights.Api.Endpoints.Kpis;
using OnVoyage.Insights.Api.Jobs;
using OnVoyage.Insights.Application.Features.Events;
using OnVoyage.Insights.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.Platform.Contracts;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddScoped<IValidator<IngestEventsCommand>, IngestEventsValidator>();
builder.Services.AddInsightsInfrastructure(builder.Configuration);
builder.Services.AddHostedService<InsightsScheduler>();

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(IngestEventsCommand).Assembly);
    options.AddOnVoyageMessaging(connectionString, "insights");

    // Answers to Platform's deletion and export requests go to Platform's queue (§13).
    options.PublishMessage<TravelerDataDeletedV1>().ToPostgresqlQueue("platform");
    options.PublishMessage<TravelerExportPartReadyV1>().ToPostgresqlQueue("platform");
});

var app = builder.Build();

await app.Services.InitializeInsightsAsync();

app.UseExceptionHandler();
app.UseOnVoyageAuthentication();
app.MapEventEndpoints();
app.MapKpiEndpoints();
app.MapDefaultEndpoints();

app.Run();

using FluentValidation;
using OnVoyage.Discovery.Api.Endpoints;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddScoped<IValidator<IngestInteractionsCommand>, IngestInteractionsValidator>();
builder.Services.AddDiscoveryInfrastructure(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(IngestInteractionsCommand).Assembly);
    options.AddOnVoyageMessaging(connectionString, "discovery");

    // Answers to Platform's deletion and export requests (T-507).
    options.PublishMessage<OnVoyage.Platform.Contracts.TravelerDataDeletedV1>().ToPostgresqlQueue("platform");
    options.PublishMessage<OnVoyage.Platform.Contracts.TravelerExportPartReadyV1>().ToPostgresqlQueue("platform");
});

var app = builder.Build();

await app.Services.InitializeDiscoveryAsync();

app.UseExceptionHandler();
app.UseOnVoyageAuthentication();
app.MapDiscoveryEndpoints();
app.MapDefaultEndpoints();

app.Run();

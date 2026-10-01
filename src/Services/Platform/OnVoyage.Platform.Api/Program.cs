using OnVoyage.Messaging;
using OnVoyage.Platform.Api.Endpoints;
using OnVoyage.Platform.Application.Features.Config;
using OnVoyage.Platform.Application.Features.DataRights;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Infrastructure;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddPlatformInfrastructure(builder.Configuration, builder.Environment);

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(GetClientConfigQuery).Assembly);
    options.AddOnVoyageMessaging(connectionString, "platform");

    // Consumers of Platform events: one PostgreSQL queue per service (§13). Services are added as they are built.
    foreach (var subscriber in builder.Configuration.GetSection("Messaging:ConfigSubscribers").Get<string[]>() ?? ["catalog"])
    {
        options.PublishMessage<ConfigChangedV1>().ToPostgresqlQueue(subscriber);
    }

    // Data rights (T-507): Platform asks the services that hold traveler data; they answer on the "platform" queue.
    foreach (var service in builder.Configuration.GetSection("Messaging:DataRightsSubscribers").Get<string[]>() ?? ["discovery", "factory", "insights", "creators"])
    {
        options.PublishMessage<TravelerDeletionRequestedV1>().ToPostgresqlQueue(service);
        options.PublishMessage<TravelerExportRequestedV1>().ToPostgresqlQueue(service);
    }

    foreach (var subscriber in builder.Configuration.GetSection("Messaging:ConsentSubscribers").Get<string[]>() ?? ["insights"])
    {
        options.PublishMessage<ConsentChangedV1>().ToPostgresqlQueue(subscriber);
    }
});

builder.Services.AddPlatformInitializer();

var app = builder.Build();

app.UseExceptionHandler();
app.UseOnVoyageAuthentication();
app.MapPlatformEndpoints();
app.MapDefaultEndpoints();

app.Run();


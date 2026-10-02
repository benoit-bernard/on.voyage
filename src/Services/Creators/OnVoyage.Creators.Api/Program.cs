using OnVoyage.Creators.Api.Background;
using OnVoyage.Creators.Api.Endpoints;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Infrastructure;
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
builder.Services.AddCreatorsInfrastructure(builder.Configuration);
if (builder.Configuration.GetValue("Creators:Social:SyncEnabled", false))
{
    builder.Services.AddHostedService<SocialSyncScheduler>();
}


var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(CreatorAdminHandler).Assembly);
    options.AddOnVoyageMessaging(connectionString, "creators");

    // Platform: answers to its deletion and export requests, the admin journal (SEC-10) and the creator role (CreatorTermsAcceptedV1).
    options.PublishMessage<TravelerDataDeletedV1>().ToPostgresqlQueue("platform");
    options.PublishMessage<TravelerExportPartReadyV1>().ToPostgresqlQueue("platform");
    options.PublishMessage<AdminActionRecordedV1>().ToPostgresqlQueue("platform");
    options.PublishMessage<CreatorTermsAcceptedV1>().ToPostgresqlQueue("platform");

    // Consumers of the creator events: one queue per subscribed service. Discovery handles them since T-1205; Insights joins with T-1212 (configuration, not code).
    foreach (var subscriber in builder.Configuration.GetSection("Messaging:CreatorSubscribers").Get<string[]>() ?? ["discovery"])
    {
        options.PublishMessage<CreatorPublishedV1>().ToPostgresqlQueue(subscriber);
        options.PublishMessage<CreatorUnpublishedV1>().ToPostgresqlQueue(subscriber);
        options.PublishMessage<CreatorPlaceLinkChangedV1>().ToPostgresqlQueue(subscriber);
        options.PublishMessage<FollowChangedV1>().ToPostgresqlQueue(subscriber);
    }
});

var app = builder.Build();

await app.Services.InitializeCreatorsAsync();

app.UseExceptionHandler();
app.UseOnVoyageAuthentication();
app.MapCreatorsEndpoints();
app.MapDefaultEndpoints();

app.Run();

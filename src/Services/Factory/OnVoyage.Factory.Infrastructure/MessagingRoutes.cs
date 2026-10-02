using Microsoft.Extensions.Configuration;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Batches;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Contracts;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace OnVoyage.Factory.Infrastructure;

public static class MessagingRoutes
{
    public const string JobQueue = "factory";

    /// <summary>Pipeline steps go through the durable job queue, whichever host sends them.</summary>
    public static WolverineOptions RouteFactoryJobs(this WolverineOptions options)
    {
        options.PublishMessage<ImportPlacesCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<EnrichPlacesCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<ScorePlacesCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<FetchSourcesCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<ExtractFactsCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<WriteStoryCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<GenerateAudioCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<RunBatchJobCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<OnVoyage.Factory.Application.Features.Snapshot.ImportSnapshotCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<OnVoyage.Factory.Application.Features.Bootstrap.BootstrapDestinationCommand>().ToPostgresqlQueue(JobQueue);
        options.PublishMessage<OnVoyage.Factory.Application.Features.Packs.BuildPackCommand>().ToPostgresqlQueue(JobQueue);
        return options;
    }

    /// <summary>Provider outages are retried with growing delays, then the job lands in the dead-letter queue (§8.6).</summary>
    public static Action<WolverineOptions> RetryProviderFailures(IConfiguration configuration) => options =>
    {
        var seconds = configuration.GetSection("Factory:Retry:DelaysSeconds").Get<double[]>() is { Length: > 0 } configured ? configured : [10, 60, 300];
        options.OnException<ExternalServiceException>().RetryWithCooldown([.. seconds.Select(delay => TimeSpan.FromSeconds(delay))]);
    };

    /// <summary>Catalog consumes place publication events from its own queue (§9.3).</summary>
    public static WolverineOptions RouteFactoryEvents(this WolverineOptions options)
    {
        options.PublishMessage<OnVoyage.Platform.Contracts.AdminActionRecordedV1>().ToPostgresqlQueue("platform");
        options.PublishMessage<OnVoyage.Platform.Contracts.TravelerDataDeletedV1>().ToPostgresqlQueue("platform");
        options.PublishMessage<OnVoyage.Platform.Contracts.TravelerExportPartReadyV1>().ToPostgresqlQueue("platform");
        options.PublishMessage<PoiPublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<PoiUnpublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryPublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryUnpublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryArchivedV1>().ToPostgresqlQueue("catalog");

        // Discovery projects places (for the learning rule) and the onboarding clips. One event reaches both queues.
        options.PublishMessage<PoiPublishedV1>().ToPostgresqlQueue("discovery");
        options.PublishMessage<PoiUnpublishedV1>().ToPostgresqlQueue("discovery");
        options.PublishMessage<StoryPublishedV1>().ToPostgresqlQueue("discovery");
        options.PublishMessage<StoryUnpublishedV1>().ToPostgresqlQueue("discovery");
        options.PublishMessage<StoryArchivedV1>().ToPostgresqlQueue("discovery");
        return options;
    }
}

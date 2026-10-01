using Wolverine.ErrorHandling;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Contracts;
using Wolverine;
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
        return options;
    }

    /// <summary>Provider outages are retried with growing delays, then the job lands in the dead-letter queue (§8.6).</summary>
    public static void RetryProviderFailures(WolverineOptions options) =>
        options.OnException<ExternalServiceException>().RetryWithCooldown(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5));

    /// <summary>Catalog consumes place publication events from its own queue (§9.3).</summary>
    public static WolverineOptions RouteFactoryEvents(this WolverineOptions options)
    {
        options.PublishMessage<PoiPublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<PoiUnpublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryPublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryUnpublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<StoryArchivedV1>().ToPostgresqlQueue("catalog");
        return options;
    }
}

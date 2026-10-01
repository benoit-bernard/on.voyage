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
        return options;
    }

    /// <summary>Catalog consumes place publication events from its own queue (§9.3).</summary>
    public static WolverineOptions RouteFactoryEvents(this WolverineOptions options)
    {
        options.PublishMessage<PoiPublishedV1>().ToPostgresqlQueue("catalog");
        options.PublishMessage<PoiUnpublishedV1>().ToPostgresqlQueue("catalog");
        return options;
    }
}

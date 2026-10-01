using JasperFx;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace OnVoyage.Messaging;

/// <summary>
/// Wolverine setup shared by every service (cahier des charges §9.5, §13): durable inbox and outbox in the service's own
/// schema, PostgreSQL queue transport (one queue per consuming service), transactional handlers and error policies.
/// </summary>
public static class MessagingExtensions
{
    public const string TransportSchema = "wolverine_queues";

    public static WolverineOptions AddOnVoyageMessaging(this WolverineOptions options, string connectionString, string serviceName)
    {
        // Envelope storage lives in the service's own schema; the queues are shared so services can publish to each other.
        options.UsePostgresqlPersistenceAndTransport(connectionString, serviceName, TransportSchema).AutoProvision();
        options.ListenToPostgresqlQueue(serviceName);

        options.Policies.UseDurableInboxOnAllListeners();
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        options.Policies.AutoApplyTransactions();

        // Ports are implemented over DbContexts registered by factory, which Wolverine can only reach by service location.
        // Allowed (and logged) rather than forbidden; handlers themselves stay free of infrastructure types.
        options.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
        options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;

        // Transient infrastructure faults are retried with a growing cooldown; anything else is parked, never lost.
        options.OnException<TimeoutException>().RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
        options.OnException<Npgsql.NpgsqlException>().RetryWithCooldown(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        options.OnAnyException().MoveToErrorQueue();

        return options;
    }
}

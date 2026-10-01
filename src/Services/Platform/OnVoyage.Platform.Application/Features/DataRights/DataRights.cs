using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Platform.Application.Features.DataRights;

public sealed record DeletionRecord(Guid TravelerId, DateTimeOffset RequestedAt, IReadOnlyList<string> RequiredServices, IReadOnlyList<string> Acknowledged);

public sealed record ExportRecord(Guid Id, Guid TravelerId, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, IReadOnlyList<string> RequiredServices, IReadOnlyList<ExportPart> Parts);

public sealed record ExportPart(string Service, string Path);

/// <summary>Everything Platform needs to run the right to be forgotten and the right to a copy (F-22, §13, T-507). Writes publish their events in the same transaction.</summary>
public interface IDataRightsStore
{
    Task<DeletionRecord?> FindDeletionAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Records the request and publishes one <see cref="TravelerDeletionRequestedV1"/> to each required service, atomically.</summary>
    Task StartDeletionAsync(DeletionRecord record, IReadOnlyList<TravelerDeletionRequestedV1> events, CancellationToken cancellationToken);

    /// <summary>Notes that a service answered. Returns the updated record, or null when no deletion is pending for the traveler.</summary>
    Task<DeletionRecord?> AcknowledgeAsync(Guid travelerId, string service, CancellationToken cancellationToken);

    /// <summary>Deletes everything Platform holds about the traveler (account, consents, sessions, codes, requests) and leaves an anonymous completion mark.</summary>
    Task CompleteDeletionAsync(Guid travelerId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ExportRecord?> FindExportAsync(Guid exportId, CancellationToken cancellationToken);

    Task StartExportAsync(ExportRecord record, IReadOnlyList<TravelerExportRequestedV1> events, CancellationToken cancellationToken);

    /// <summary>Records one service's part (idempotent per service).</summary>
    Task<ExportRecord?> RecordPartAsync(Guid exportId, string service, string path, CancellationToken cancellationToken);

    /// <summary>Platform's own part of the archive: account and consents.</summary>
    Task<string> ExportOwnDataAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Removes expired exports (rows) and returns their ids so the files can go too.</summary>
    Task<IReadOnlyList<Guid>> DeleteExpiredExportsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Anonymous accounts nobody opened since <paramref name="cutoff"/> (retention, §16.2).</summary>
    Task<IReadOnlyList<Guid>> FindInactiveAnonymousAccountsAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);
}

/// <summary>Where each service's export part lives and how it is read; the archive is built from them.</summary>
public interface IExportFiles
{
    Task<string> ReadAsync(string path, CancellationToken cancellationToken);

    void Delete(Guid exportId);
}

public sealed record DataRightsSettings(IReadOnlyList<string> RequiredServices, TimeSpan ExportLifetime, int AnonymousInactiveMonths)
{
    public static readonly string[] DefaultServices = ["discovery", "factory", "insights", "creators"];
}

public static class DataRightsSettingsReader
{
    /// <summary>
    /// <c>deletion.required_services</c> for the current phase (annexe E) from the remote configuration; <c>retention</c> and <c>privacy</c>
    /// timings likewise. <c>Platform:DeletionRequiredServices</c> overrides the list for deployments that do not run every service yet.
    /// </summary>
    public static async Task<DataRightsSettings> ReadAsync(IRemoteConfigStore config, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var services = configuration.GetSection("Platform:DeletionRequiredServices").Get<string[]>();
        if (services is not { Length: > 0 })
        {
            var phase = configuration["Platform:Phase"] ?? "MVP-0";
            var entry = await config.FindAsync("deletion", cancellationToken);
            services = entry is not null
                && JsonDocument.Parse(entry.ValueJson).RootElement.TryGetProperty("required_services", out var byPhase)
                && byPhase.TryGetProperty(phase, out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Select(item => item.GetString()!)]
                    : DataRightsSettings.DefaultServices;
        }

        var hours = 24;
        if (await config.FindAsync("privacy", cancellationToken) is { } privacy && JsonDocument.Parse(privacy.ValueJson).RootElement.TryGetProperty("export_link_hours", out var h) && h.TryGetInt32(out var value))
        {
            hours = value;
        }

        var months = 24;
        if (await config.FindAsync("retention", cancellationToken) is { } retention && JsonDocument.Parse(retention.ValueJson).RootElement.TryGetProperty("anonymous_inactive_months", out var m) && m.TryGetInt32(out var parsed))
        {
            months = parsed;
        }

        return new DataRightsSettings(services, TimeSpan.FromHours(hours), months);
    }
}

public sealed record RequestDeletionCommand(Guid TravelerId);

public sealed record GetDeletionQuery(Guid TravelerId);

public static class DeletionHandlers
{
    internal static DeletionStatusDto ToDto(DeletionRecord record) =>
        new("pending", record.RequestedAt, null, [.. record.RequiredServices.Except(record.Acknowledged)]);
}

public static class RequestDeletionHandler
{
    /// <summary>Asking twice returns the request already under way: nothing is published again.</summary>
    public static async Task<Result<DeletionStatusDto>> Handle(RequestDeletionCommand command, IDataRightsStore store, IRemoteConfigStore config, IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await store.FindDeletionAsync(command.TravelerId, cancellationToken) is { } existing)
        {
            return Result.Success(DeletionHandlers.ToDto(existing));
        }

        var settings = await DataRightsSettingsReader.ReadAsync(config, configuration, cancellationToken);
        var now = clock.GetUtcNow();
        var record = new DeletionRecord(command.TravelerId, now, settings.RequiredServices, []);
        await store.StartDeletionAsync(record, [new TravelerDeletionRequestedV1(Guid.CreateVersion7(), now, command.TravelerId, now)], cancellationToken);
        return Result.Success(DeletionHandlers.ToDto(record));
    }
}

public static class GetDeletionHandler
{
    public static async Task<Result<DeletionStatusDto>> Handle(GetDeletionQuery query, IDataRightsStore store, CancellationToken cancellationToken) =>
        await store.FindDeletionAsync(query.TravelerId, cancellationToken) is { } record
            ? Result.Success(DeletionHandlers.ToDto(record))
            : Result.Failure<DeletionStatusDto>("deletion_not_found", "No deletion in progress.");
}

public static class TravelerDataDeletedHandler
{
    /// <summary>Once every required service has answered, Platform deletes its own data last: the account is the key that ties the rest together.</summary>
    public static async Task Handle(TravelerDataDeletedV1 deleted, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var record = await store.AcknowledgeAsync(deleted.TravelerId, deleted.Service, cancellationToken);
        if (record is not null && record.RequiredServices.All(record.Acknowledged.Contains))
        {
            await store.CompleteDeletionAsync(deleted.TravelerId, clock.GetUtcNow(), cancellationToken);
        }
    }
}

public sealed record RequestExportCommand(Guid TravelerId);

public sealed record GetExportQuery(Guid TravelerId, Guid ExportId);

public sealed record GetExportArchiveQuery(Guid TravelerId, Guid ExportId);

public static class ExportHandlers
{
    internal static ExportStatusDto ToDto(ExportRecord record, DateTimeOffset now)
    {
        var pending = record.RequiredServices.Except(record.Parts.Select(p => p.Service)).ToArray();
        var status = now >= record.ExpiresAt ? "expired" : pending.Length == 0 ? "ready" : "pending";
        return new ExportStatusDto(record.Id, status, record.RequestedAt, status == "ready" ? record.ExpiresAt : null, pending);
    }
}

public static class RequestExportHandler
{
    public static async Task<Result<ExportStatusDto>> Handle(RequestExportCommand command, IDataRightsStore store, IRemoteConfigStore config, IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        var settings = await DataRightsSettingsReader.ReadAsync(config, configuration, cancellationToken);
        var now = clock.GetUtcNow();
        var record = new ExportRecord(Guid.CreateVersion7(), command.TravelerId, now, now + settings.ExportLifetime, settings.RequiredServices, []);
        await store.StartExportAsync(record, [new TravelerExportRequestedV1(Guid.CreateVersion7(), now, record.Id, command.TravelerId)], cancellationToken);
        return Result.Success(ExportHandlers.ToDto(record, now));
    }
}

public static class GetExportHandler
{
    public static async Task<Result<ExportStatusDto>> Handle(GetExportQuery query, IDataRightsStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        // Somebody else's export is "not found", not "forbidden": its existence is not ours to confirm.
        var record = await store.FindExportAsync(query.ExportId, cancellationToken);
        return record is null || record.TravelerId != query.TravelerId
            ? Result.Failure<ExportStatusDto>("export_not_found", "Unknown export.")
            : Result.Success(ExportHandlers.ToDto(record, clock.GetUtcNow()));
    }
}

public static class TravelerExportPartReadyHandler
{
    public static Task Handle(TravelerExportPartReadyV1 ready, IDataRightsStore store, CancellationToken cancellationToken) =>
        store.RecordPartAsync(ready.ExportId, ready.Service, ready.Path, cancellationToken);
}

public static class GetExportArchiveHandler
{
    /// <summary>The archive: Platform's own data and each service's part under its name, as one JSON document.</summary>
    public static async Task<Result<string>> Handle(GetExportArchiveQuery query, IDataRightsStore store, IExportFiles files, TimeProvider clock, CancellationToken cancellationToken)
    {
        var record = await store.FindExportAsync(query.ExportId, cancellationToken);
        if (record is null || record.TravelerId != query.TravelerId)
        {
            return Result.Failure<string>("export_not_found", "Unknown export.");
        }

        var now = clock.GetUtcNow();
        if (ExportHandlers.ToDto(record, now).Status != "ready")
        {
            return Result.Failure<string>("export_not_ready", "The export is not ready or has expired.");
        }

        var services = new Dictionary<string, JsonElement>();
        foreach (var part in record.Parts.OrderBy(p => p.Service, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await files.ReadAsync(part.Path, cancellationToken));
            services[part.Service] = document.RootElement.Clone();
        }

        using var own = JsonDocument.Parse(await store.ExportOwnDataAsync(query.TravelerId, cancellationToken));
        return Result.Success(JsonSerializer.Serialize(new { exportedAt = now, travelerId = query.TravelerId, platform = own.RootElement.Clone(), services }));
    }
}

public sealed record CleanUpExportsCommand;

public static class CleanUpExportsHandler
{
    public static async Task<int> Handle(CleanUpExportsCommand command, IDataRightsStore store, IExportFiles files, TimeProvider clock, CancellationToken cancellationToken)
    {
        var expired = await store.DeleteExpiredExportsAsync(clock.GetUtcNow(), cancellationToken);
        foreach (var id in expired)
        {
            files.Delete(id);
        }

        return expired.Count;
    }
}

public sealed record PurgeInactiveAnonymousAccountsCommand;

public static class PurgeInactiveAnonymousAccountsHandler
{
    /// <summary>Anonymous accounts unused for 24 months (⚙️) go through the same deletion as an explicit request (§16.2).</summary>
    public static async Task<int> Handle(PurgeInactiveAnonymousAccountsCommand command, IDataRightsStore store, IRemoteConfigStore config, IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        var settings = await DataRightsSettingsReader.ReadAsync(config, configuration, cancellationToken);
        var now = clock.GetUtcNow();
        var cutoff = now.AddMonths(-settings.AnonymousInactiveMonths);
        var started = 0;
        foreach (var traveler in await store.FindInactiveAnonymousAccountsAsync(cutoff, 200, cancellationToken))
        {
            if (await store.FindDeletionAsync(traveler, cancellationToken) is not null)
            {
                continue;
            }

            await store.StartDeletionAsync(
                new DeletionRecord(traveler, now, settings.RequiredServices, []),
                [new TravelerDeletionRequestedV1(Guid.CreateVersion7(), now, traveler, now)],
                cancellationToken);
            started++;
        }

        return started;
    }
}

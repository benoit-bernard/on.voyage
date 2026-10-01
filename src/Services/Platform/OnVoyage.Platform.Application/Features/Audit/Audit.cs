using OnVoyage.Platform.Contracts;

namespace OnVoyage.Platform.Application.Features.Audit;

public interface IAdminAuditStore
{
    /// <summary>Stores the entry unless an entry with the same event id exists (events are delivered at least once); false for a duplicate.</summary>
    Task<bool> AddAsync(AdminActionRecordedV1 action, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminActionDto>> ListAsync(int limit, string? service, string? actor, CancellationToken cancellationToken);
}

public sealed record ListAdminAuditQuery(int Limit, string? Service, string? Actor);

/// <summary>An admin write made in Platform itself: journaled directly, the same way other services' events are.</summary>
public sealed record RecordPlatformAdminActionCommand(string Actor, string Action, string Target, int Status, string? Summary);

public static class AdminActionRecordedHandler
{
    public static async Task Handle(AdminActionRecordedV1 recorded, IAdminAuditStore store, CancellationToken cancellationToken) =>
        await store.AddAsync(recorded, cancellationToken);
}

public static class AdminAuditHandler
{
    public static async Task<Result<bool>> Handle(RecordPlatformAdminActionCommand command, IAdminAuditStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var summary = command.Summary is { Length: > 500 } text ? text[..500] : command.Summary;
        await store.AddAsync(new AdminActionRecordedV1(Guid.CreateVersion7(), clock.GetUtcNow(), "platform", command.Actor, command.Action, command.Target, command.Status, summary), cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<IReadOnlyList<AdminActionDto>>> Handle(ListAdminAuditQuery query, IAdminAuditStore store, CancellationToken cancellationToken) =>
        Result.Success(await store.ListAsync(Math.Clamp(query.Limit, 1, 500), string.IsNullOrWhiteSpace(query.Service) ? null : query.Service, string.IsNullOrWhiteSpace(query.Actor) ? null : query.Actor, cancellationToken));
}

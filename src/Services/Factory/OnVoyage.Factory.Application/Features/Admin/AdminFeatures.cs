using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Features.Admin;

public sealed record AuditEntry(Guid Id, DateTimeOffset At, string Actor, string Action, string Target, int Status, string? Detail);

public sealed record PronunciationEntry(string Destination, string Term, string Replacement);

public interface IAdminStore
{
    Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, string? actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<PronunciationEntry>> ListPronunciationsAsync(string destination, CancellationToken cancellationToken);

    Task UpsertPronunciationAsync(PronunciationEntry entry, CancellationToken cancellationToken);

    Task<bool> DeletePronunciationAsync(string destination, string term, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryRecord>> ListStoriesByStatusAsync(ContentStatus status, int limit, CancellationToken cancellationToken);
}

public sealed record ListDestinationsQuery;

public sealed record RecordAuditCommand(string Actor, string Action, string Target, int Status, string? Detail);

public sealed record ListAuditQuery(int Limit, string? Actor);

public sealed record ListPronunciationsQuery(string Destination);

public sealed record SetPronunciationCommand(string Destination, string Term, string Replacement);

public sealed record DeletePronunciationCommand(string Destination, string Term);

public sealed record ListStoriesByStatusQuery(ContentStatus Status, int Limit);

public static class AdminHandler
{
    public static async Task<Result<IReadOnlyList<DestinationConfig>>> Handle(ListDestinationsQuery query, IDestinationCatalog destinations, CancellationToken cancellationToken) =>
        Result.Success(await destinations.ListAsync(cancellationToken));

    public static async Task<Result<bool>> Handle(RecordAuditCommand command, IAdminStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var detail = command.Detail is { Length: > 500 } ? command.Detail[..500] : command.Detail;
        await store.AddAuditAsync(new AuditEntry(Guid.CreateVersion7(), clock.GetUtcNow(), command.Actor, command.Action, command.Target, command.Status, detail), cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<IReadOnlyList<AuditEntry>>> Handle(ListAuditQuery query, IAdminStore store, CancellationToken cancellationToken) =>
        Result.Success(await store.ListAuditAsync(Math.Clamp(query.Limit, 1, 500), query.Actor, cancellationToken));

    public static async Task<Result<IReadOnlyList<PronunciationEntry>>> Handle(ListPronunciationsQuery query, IAdminStore store, CancellationToken cancellationToken) =>
        Result.Success(await store.ListPronunciationsAsync(query.Destination, cancellationToken));

    public static async Task<Result<bool>> Handle(SetPronunciationCommand command, IAdminStore store, IDestinationCatalog destinations, CancellationToken cancellationToken)
    {
        var term = command.Term.Trim();
        var replacement = command.Replacement.Trim();
        if (term.Length is 0 or > 80 || replacement.Length is 0 or > 120)
        {
            return Result.Failure<bool>("validation", "The term (up to 80 characters) and its spelling (up to 120) are required.");
        }

        if (await destinations.FindAsync(command.Destination, cancellationToken) is null)
        {
            return Result.Failure<bool>("destination_not_found", "Unknown destination.");
        }

        await store.UpsertPronunciationAsync(new PronunciationEntry(command.Destination, term, replacement), cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<bool>> Handle(DeletePronunciationCommand command, IAdminStore store, CancellationToken cancellationToken) =>
        await store.DeletePronunciationAsync(command.Destination, command.Term, cancellationToken)
            ? Result.Success(true)
            : Result.Failure<bool>("pronunciation_not_found", "No such entry.");

    public static async Task<Result<IReadOnlyList<StoryRecord>>> Handle(ListStoriesByStatusQuery query, IAdminStore store, CancellationToken cancellationToken) =>
        Result.Success(await store.ListStoriesByStatusAsync(query.Status, Math.Clamp(query.Limit, 1, 200), cancellationToken));
}

using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Features;

public sealed record CreateFounderCommand(Guid Actor, CreatorProfileRequest Profile);

public sealed record UpdateCreatorCommand(Guid Actor, Guid CreatorId, CreatorProfileRequest Profile);

public sealed record RecordFounderConsentCommand(Guid Actor, Guid CreatorId, FounderConsentRequest Consent);

public sealed record LinkAccountCommand(Guid Actor, Guid CreatorId, Guid AccountId);

public sealed record PublishCreatorCommand(Guid Actor, Guid CreatorId);

public sealed record UnpublishCreatorCommand(Guid Actor, Guid CreatorId, string Reason);

public sealed record SuspendCreatorCommand(Guid Actor, Guid CreatorId, string Reason);

public sealed record ClaimHandleCommand(Guid Actor, Guid CreatorId, string Handle, string Reason);

public sealed record ListCreatorsAdminQuery(string? Status, string? Search, int Limit);

public sealed record GetCreatorAdminQuery(Guid CreatorId);

/// <summary>Founding creators: profile on consent, publication rule of F-26, suspension and handle claim (F-33). Every write is journaled in the same transaction.</summary>
public static class CreatorAdminHandler
{
    private const int MaxReason = 200;

    public static async Task<Result<IReadOnlyList<AdminCreatorSummaryDto>>> Handle(ListCreatorsAdminQuery query, ICreatorQueries queries, CancellationToken cancellationToken)
    {
        if (query.Status is not null and not (CreatorStatuses.Draft or CreatorStatuses.Published or CreatorStatuses.Suspended))
        {
            return Result.Failure<IReadOnlyList<AdminCreatorSummaryDto>>("validation", "Statut inconnu.");
        }

        return Result.Success(await queries.ListAdminAsync(query.Status, string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(), Math.Clamp(query.Limit, 1, 200), cancellationToken));
    }

    public static async Task<Result<AdminCreatorDetailDto>> Handle(GetCreatorAdminQuery query, ICreatorQueries queries, CancellationToken cancellationToken) =>
        await queries.GetAdminDetailAsync(query.CreatorId, cancellationToken) is { } detail
            ? Result.Success(detail)
            : NotFound();

    public static async Task<Result<AdminCreatorDetailDto>> Handle(CreateFounderCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (profile, violation) = CreatorProfileRules.Normalize(ToProfile(command.Profile));
        if (profile is null)
        {
            return Result.Failure<AdminCreatorDetailDto>(violation!);
        }

        if (await creators.FindByHandleAsync(profile.Handle, cancellationToken) is not null)
        {
            return await HandleTaken(profile.Handle, creators, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var creator = Creator.NewDraft(Guid.CreateVersion7(), profile, founding: true, now);
        await creators.StageAsync(creator, cancellationToken);
        try
        {
            await unit.CommitAsync([CreatorEvents.Audit(command.Actor, "creator.create", Target(creator), $"handle=@{profile.Handle}", now)], cancellationToken);
        }
        catch (UniqueConflictException)
        {
            return await HandleTaken(profile.Handle, creators, cancellationToken);
        }

        return await Detail(queries, creator.Id, cancellationToken);
    }

    public static async Task<Result<AdminCreatorDetailDto>> Handle(UpdateCreatorCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        var (profile, violation) = CreatorProfileRules.Normalize(ToProfile(command.Profile));
        if (profile is null)
        {
            return Result.Failure<AdminCreatorDetailDto>(violation!);
        }

        if (!string.Equals(profile.Handle, creator.Handle, StringComparison.OrdinalIgnoreCase)
            && await creators.FindByHandleAsync(profile.Handle, cancellationToken) is not null)
        {
            return await HandleTaken(profile.Handle, creators, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var updated = creator.WithProfile(profile, now);
        var published = creator.Status == CreatorStatuses.Published;
        if (published && updated.PublishBlock is { } block)
        {
            return Result.Failure<AdminCreatorDetailDto>(block.Code, "Cette modification rendrait le profil impubliable : dépubliez-le d'abord.");
        }

        await creators.StageAsync(updated, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "creator.update", Target(creator), $"handle=@{profile.Handle}", now)];
        if (published)
        {
            events.Add(CreatorEvents.Published(updated, now)); // the public data (handle, name, avatar, specialties) may have changed
        }

        try
        {
            await unit.CommitAsync(events, cancellationToken);
        }
        catch (UniqueConflictException)
        {
            return await HandleTaken(profile.Handle, creators, cancellationToken);
        }

        return await Detail(queries, creator.Id, cancellationToken);
    }

    /// <summary>The written consent of a founder stands for the acceptance of the creator terms (F-26). It needs the reference of the signed document.</summary>
    public static async Task<Result<AdminCreatorDetailDto>> Handle(RecordFounderConsentCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var reference = command.Consent.DocumentRef?.Trim() ?? string.Empty;
        if (reference.Length is 0 or > 200)
        {
            return Result.Failure<AdminCreatorDetailDto>("validation", "La référence du document de consentement signé est obligatoire (200 caractères au plus).");
        }

        var acceptedAt = command.Consent.AcceptedAt ?? now;
        if (acceptedAt > now)
        {
            return Result.Failure<AdminCreatorDetailDto>("validation", "La date du consentement ne peut pas être dans le futur.");
        }

        var updated = creator.WithFounderConsent(reference, acceptedAt, now);
        await creators.StageAsync(updated, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "creator.consent", Target(creator), $"terms=fondateur; ref={reference}", now)];
        if (CreatorEvents.TermsAccepted(updated, now) is { } accepted)
        {
            events.Add(accepted);
        }

        await unit.CommitAsync(events, cancellationToken);
        return await Detail(queries, creator.Id, cancellationToken);
    }

    /// <summary>Links the ON.VOYAGE account of the creator. With the terms accepted, Platform adds the <c>creator</c> role to it.</summary>
    public static async Task<Result<AdminCreatorDetailDto>> Handle(LinkAccountCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        if (command.AccountId == Guid.Empty)
        {
            return Result.Failure<AdminCreatorDetailDto>("validation", "L'identifiant du compte est obligatoire.");
        }

        if (await creators.FindByAccountAsync(command.AccountId, cancellationToken) is { } other && other.Id != creator.Id)
        {
            return Result.Failure<AdminCreatorDetailDto>("account_in_use", "Ce compte est déjà rattaché à un autre créateur.");
        }

        var now = clock.GetUtcNow();
        var updated = creator.WithAccount(command.AccountId, now);
        await creators.StageAsync(updated, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "creator.link_account", Target(creator), $"account={command.AccountId}", now)];
        if (CreatorEvents.TermsAccepted(updated, now) is { } accepted)
        {
            events.Add(accepted);
        }

        try
        {
            await unit.CommitAsync(events, cancellationToken);
        }
        catch (UniqueConflictException)
        {
            return Result.Failure<AdminCreatorDetailDto>("account_in_use", "Ce compte est déjà rattaché à un autre créateur.");
        }

        return await Detail(queries, creator.Id, cancellationToken);
    }

    public static async Task<Result<AdminCreatorDetailDto>> Handle(PublishCreatorCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        if (creator.Status == CreatorStatuses.Published)
        {
            return await Detail(queries, creator.Id, cancellationToken);
        }

        if (creator.PublishBlock is { } block)
        {
            return Result.Failure<AdminCreatorDetailDto>(block);
        }

        var now = clock.GetUtcNow();
        var published = creator.Published(now);
        await creators.StageAsync(published, cancellationToken);
        await unit.CommitAsync([CreatorEvents.Audit(command.Actor, "creator.publish", Target(creator), null, now), CreatorEvents.Published(published, now)], cancellationToken);
        return await Detail(queries, creator.Id, cancellationToken);
    }

    public static async Task<Result<AdminCreatorDetailDto>> Handle(UnpublishCreatorCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (CheckReason(command.Reason) is { } violation)
        {
            return Result.Failure<AdminCreatorDetailDto>(violation);
        }

        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        if (creator.Status != CreatorStatuses.Published)
        {
            return Result.Failure<AdminCreatorDetailDto>("not_published", "Ce profil n'est pas publié.");
        }

        var now = clock.GetUtcNow();
        await creators.StageAsync(creator.Unpublished(now), cancellationToken);
        await unit.CommitAsync(
            [CreatorEvents.Audit(command.Actor, "creator.unpublish", Target(creator), $"reason={command.Reason.Trim()}", now), CreatorEvents.Unpublished(creator, command.Reason.Trim(), now)],
            cancellationToken);
        return await Detail(queries, creator.Id, cancellationToken);
    }

    public static async Task<Result<AdminCreatorDetailDto>> Handle(SuspendCreatorCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (CheckReason(command.Reason) is { } violation)
        {
            return Result.Failure<AdminCreatorDetailDto>(violation);
        }

        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } creator)
        {
            return NotFound();
        }

        if (creator.Status == CreatorStatuses.Suspended)
        {
            return await Detail(queries, creator.Id, cancellationToken);
        }

        var now = clock.GetUtcNow();
        await creators.StageAsync(creator.Suspended(now), cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "creator.suspend", Target(creator), $"reason={command.Reason.Trim()}", now)];
        if (creator.Status == CreatorStatuses.Published)
        {
            events.Add(CreatorEvents.Unpublished(creator, command.Reason.Trim(), now));
        }

        await unit.CommitAsync(events, cancellationToken);
        return await Detail(queries, creator.Id, cancellationToken);
    }

    /// <summary>
    /// Handle claim (F-33, impersonation): the true holder of a handle takes it from the creator who has it. The previous holder is
    /// unpublished and renamed with the first free variant; both renames happen in one transaction.
    /// </summary>
    public static async Task<Result<AdminCreatorDetailDto>> Handle(ClaimHandleCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (CheckReason(command.Reason) is { } violation)
        {
            return Result.Failure<AdminCreatorDetailDto>(violation);
        }

        if (!Handles.TryNormalize(command.Handle, out var handle))
        {
            return Result.Failure<AdminCreatorDetailDto>("invalid_handle", $"Le handle doit faire de {Handles.MinLength} à {Handles.MaxLength} caractères (lettres, chiffres, point et tiret bas).");
        }

        if (await creators.FindAsync(command.CreatorId, cancellationToken) is not { } claimant)
        {
            return NotFound();
        }

        var holder = await creators.FindByHandleAsync(handle, cancellationToken);
        if (holder?.Id == claimant.Id)
        {
            return await Detail(queries, claimant.Id, cancellationToken);
        }

        var now = clock.GetUtcNow();
        List<object> events = [];
        if (holder is not null)
        {
            var taken = await creators.HandlesStartingWithAsync(handle[..Math.Min(handle.Length, Handles.MaxLength - 4)], cancellationToken);
            var renamed = holder.WithHandle(Handles.Suggest(handle, taken.Contains), now);
            if (holder.Status == CreatorStatuses.Published)
            {
                events.Add(CreatorEvents.Unpublished(holder, "handle_claimed", now));
                renamed = renamed.Unpublished(now);
            }

            await creators.StageAsync(renamed, cancellationToken);
            await unit.FlushAsync(cancellationToken); // the handle must be free before the claimant takes it
            events.Add(CreatorEvents.Audit(command.Actor, "creator.claim_handle", Target(claimant), $"handle=@{handle}; previous holder {holder.Id} renamed @{renamed.Handle}; reason={command.Reason.Trim()}", now));
        }
        else
        {
            events.Add(CreatorEvents.Audit(command.Actor, "creator.claim_handle", Target(claimant), $"handle=@{handle}; reason={command.Reason.Trim()}", now));
        }

        var updated = claimant.WithHandle(handle, now);
        await creators.StageAsync(updated, cancellationToken);
        if (claimant.Status == CreatorStatuses.Published)
        {
            events.Add(CreatorEvents.Published(updated, now));
        }

        await unit.CommitAsync(events, cancellationToken);
        return await Detail(queries, claimant.Id, cancellationToken);
    }

    private static CreatorProfile ToProfile(CreatorProfileRequest request) =>
        new(
            request.Handle,
            request.DisplayName,
            request.Bio,
            request.AvatarPath,
            request.Languages ?? [],
            request.Specialties ?? [],
            request.DestinationIds ?? [],
            [.. (request.Links ?? []).Select(link => new CreatorLink(link.Kind ?? string.Empty, link.Url ?? string.Empty))]);

    private static string Target(Creator creator) => $"creator:{creator.Id}";

    private static Violation? CheckReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReason ? new Violation("validation", $"Le motif est obligatoire ({MaxReason} caractères au plus).") : null;

    private static Result<AdminCreatorDetailDto> NotFound() => Result.Failure<AdminCreatorDetailDto>("creator_not_found", "Créateur introuvable.");

    private static async Task<Result<AdminCreatorDetailDto>> Detail(ICreatorQueries queries, Guid id, CancellationToken cancellationToken) =>
        Result.Success((await queries.GetAdminDetailAsync(id, cancellationToken))!);

    private static async Task<Result<AdminCreatorDetailDto>> HandleTaken(string handle, ICreatorRepository creators, CancellationToken cancellationToken)
    {
        var taken = await creators.HandlesStartingWithAsync(handle[..Math.Min(handle.Length, Handles.MaxLength - 4)], cancellationToken);
        var suggestion = Handles.Suggest(handle, taken.Contains);
        return Result.Failure<AdminCreatorDetailDto>("handle_taken", $"Le handle @{handle} est déjà pris. Suggestion : @{suggestion}.", suggestion);
    }
}

using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using Wolverine;

namespace OnVoyage.Creators.Application.Features;

public sealed record ListConnectionsQuery(Guid AccountId);

public sealed record StartConnectionQuery(Guid AccountId, string Platform);

public sealed record CompleteConnectionCommand(Guid AccountId, string Platform, CompleteConnectionRequest Request);

public sealed record DisconnectCommand(Guid AccountId, string Platform, bool DeleteContents);

public sealed record RequestSyncCommand(Guid AccountId);

/// <summary>Internal message: import (or refresh) what a connected account has published. Sent after a connection, on "Resynchroniser" and by the daily schedule.</summary>
public sealed record SyncConnectedAccountCommand(Guid ConnectedAccountId);

public static class ThumbnailPaths
{
    /// <summary>Covers that ON.VOYAGE copied live under this folder of the media root; any other cover path was typed by a person and is never deleted by us.</summary>
    public const string Prefix = "creators/";

    public static bool IsOurs(string? coverPath) => coverPath is not null && coverPath.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// Connecting a social account (F-27): optional, server side, started by the creator. The tokens never reach this layer (<see cref="ISocialConnector"/>);
/// disconnecting revokes them at the platform when it can, deletes them, deletes the thumbnails that were copied, and, if asked, withdraws the contents.
/// </summary>
public static class ConnectionsHandler
{
    public static async Task<Result<ConnectionsDto>> Handle(ListConnectionsQuery query, ICreatorRepository creators, IConnectedAccountRepository accounts, ISocialConnector connector, CancellationToken cancellationToken)
    {
        if (await creators.FindByAccountAsync(query.AccountId, cancellationToken) is not { } creator)
        {
            return Result.Failure<ConnectionsDto>("creator_not_found", "Aucun espace créateur pour ce compte.");
        }

        var connected = await accounts.ListAsync(creator.Id, cancellationToken);
        var counts = await accounts.CountContentsAsync([.. connected.Select(account => account.Id)], cancellationToken);
        return Result.Success(new ConnectionsDto([.. ConnectablePlatforms.All.Select(platform =>
            new ConnectionPlatformDto(platform, connector.IsEnabled(platform), connected.FirstOrDefault(account => account.Platform == platform) is { } account ? ToDto(account, counts.GetValueOrDefault(account.Id)) : null))]));
    }

    public static async Task<Result<ConnectionStartDto>> Handle(StartConnectionQuery query, ICreatorRepository creators, ISocialConnector connector, CancellationToken cancellationToken)
    {
        if (!ConnectablePlatforms.IsKnown(query.Platform))
        {
            return Result.Failure<ConnectionStartDto>("validation", "Plateforme inconnue (instagram, youtube).");
        }

        var (creator, failure) = await StudioHandler.Own<ConnectionStartDto>(query.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        return connector.IsEnabled(query.Platform)
            ? Result.Success(new ConnectionStartDto(connector.Begin(query.Platform, creator.Id).AbsoluteUri))
            : Disabled<ConnectionStartDto>();
    }

    public static async Task<Result<ConnectedAccountDto>> Handle(CompleteConnectionCommand command, ICreatorRepository creators, ISocialConnector connector, IMessageBus bus, CancellationToken cancellationToken)
    {
        if (!ConnectablePlatforms.IsKnown(command.Platform))
        {
            return Result.Failure<ConnectedAccountDto>("validation", "Plateforme inconnue (instagram, youtube).");
        }

        var (creator, failure) = await StudioHandler.Own<ConnectedAccountDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var outcome = await connector.CompleteAsync(command.Platform, creator.Id, command.Request.Code, command.Request.State, cancellationToken);
        switch (outcome.Refusal)
        {
            case ConnectRefusal.None:
                // The first import runs in the background: the creator sees the account at once and its contents a moment later.
                await bus.PublishAsync(new SyncConnectedAccountCommand(outcome.Account!.Id));
                return Result.Success(ToDto(outcome.Account, 0));
            case ConnectRefusal.ProfessionalAccountRequired:
                return Result.Failure<ConnectedAccountDto>("professional_account_required", "Compte professionnel requis : passez votre compte Instagram en compte professionnel (Business ou Créateur) puis reconnectez-le. Aide Meta : https://help.instagram.com/502981923235522");
            case ConnectRefusal.AccessDenied:
                return Result.Failure<ConnectedAccountDto>("access_denied", "L'autorisation n'a pas été donnée : rien n'est connecté.");
            case ConnectRefusal.InvalidState:
                return Result.Failure<ConnectedAccountDto>("invalid_state", "Cette demande de connexion a expiré ou n'est pas la vôtre : recommencez depuis l'espace créateur.");
            case ConnectRefusal.AccountInUse:
                return Result.Failure<ConnectedAccountDto>("account_in_use", "Ce compte est déjà connecté à un autre créateur ON.VOYAGE. S'il s'agit du vôtre, contactez l'équipe (usurpation).");
            case ConnectRefusal.Disabled:
                return Disabled<ConnectedAccountDto>();
            default:
                return Result.Failure<ConnectedAccountDto>("provider_error", "La plateforme n'a pas répondu comme prévu : réessayez dans un instant.");
        }
    }

    public static async Task<Result<bool>> Handle(DisconnectCommand command, ICreatorRepository creators, IConnectedAccountRepository accounts, IContentRepository contents, ISocialConnector connector, IThumbnailStore thumbnails, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await creators.FindByAccountAsync(command.AccountId, cancellationToken) is not { } creator)
        {
            return Result.Failure<bool>("creator_not_found", "Aucun espace créateur pour ce compte.");
        }

        if (await accounts.FindAsync(creator.Id, command.Platform, cancellationToken) is not { } account)
        {
            return Result.Failure<bool>("connection_not_found", "Ce compte n'est pas connecté.");
        }

        var now = clock.GetUtcNow();
        await connector.RevokeAsync(account.Id, cancellationToken);

        List<object> events = [];
        foreach (var content in await contents.ListContentsOfAccountAsync(account.Id, cancellationToken))
        {
            // The copies of the thumbnails go with the connection (licence of the creator terms, F-27).
            if (ThumbnailPaths.IsOurs(content.CoverPath))
            {
                await thumbnails.DeleteAsync(content.CoverPath!, cancellationToken);
            }

            var updated = content with { CoverPath = ThumbnailPaths.IsOurs(content.CoverPath) ? null : content.CoverPath, ConnectedAccountId = null };
            if (command.DeleteContents && content.Status != ContentStatuses.Removed)
            {
                updated = updated with { Status = ContentStatuses.Removed };
                if (content.IsOnline)
                {
                    events.AddRange((await contents.ListLinksOfContentAsync(content.Id, cancellationToken)).Where(link => link.IsValidated).Select(link => CreatorEvents.LinkChanged(link, updated, false, now)));
                }
            }

            await contents.StageContentAsync(updated, cancellationToken);
        }

        await accounts.DeleteAsync(account.Id, cancellationToken);
        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<SyncRequestedDto>> Handle(RequestSyncCommand command, ICreatorRepository creators, IConnectedAccountRepository accounts, IMessageBus bus, CancellationToken cancellationToken)
    {
        var (creator, failure) = await StudioHandler.Own<SyncRequestedDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var active = (await accounts.ListAsync(creator.Id, cancellationToken)).Where(account => account.IsActive).ToList();
        foreach (var account in active)
        {
            await bus.PublishAsync(new SyncConnectedAccountCommand(account.Id));
        }

        return Result.Success(new SyncRequestedDto(active.Count));
    }

    internal static ConnectedAccountDto ToDto(ConnectedAccount account, int contentCount) =>
        new(account.Id, account.Platform, account.Username, account.Status, account.LastSyncAt, contentCount, account.LastError, account.Scopes);

    private static Result<T> Disabled<T>() =>
        Result.Failure<T>("connections_disabled", "Les imports ne sont pas encore ouverts pour cette plateforme (validation de l'application en cours). Ajoutez vos contenus par leur adresse.");
}

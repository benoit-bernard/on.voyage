using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Features;

public sealed record GetRegistrationQuery(Guid AccountId);

public sealed record StudioSignupCommand(Guid AccountId, StudioSignupRequest Request);

public sealed record AcceptTermsCommand(Guid AccountId, string Version);

public sealed record GetStudioProfileQuery(Guid AccountId);

public sealed record UpdateStudioProfileCommand(Guid AccountId, CreatorProfileRequest Profile);

public sealed record PublishStudioCommand(Guid AccountId);

public sealed record UnpublishStudioCommand(Guid AccountId);

public sealed record StudioAddContentCommand(Guid AccountId, AddContentRequest Content);

public sealed record StudioUpdateContentCommand(Guid AccountId, Guid ContentId, UpdateContentRequest Content);

public sealed record StudioRemoveContentCommand(Guid AccountId, Guid ContentId);

public sealed record StudioAddPlaceLinkCommand(Guid AccountId, AddPlaceLinkRequest Link);

public sealed record StudioSetPlaceLinkStatusCommand(Guid AccountId, Guid LinkId, string Status);

public sealed record StudioRemovePlaceLinkCommand(Guid AccountId, Guid LinkId);

public sealed record StudioSetTipCommand(Guid AccountId, Guid PoiId, string Text);

public sealed record StudioRemoveTipCommand(Guid AccountId, Guid PoiId);

public sealed record StudioSearchPlacesQuery(string Query, string? Destination);

/// <summary>
/// The creator's own space (F-26, T-1206): sign-up with the creator terms, profile, publication, tips and the creator's contents. The creator is
/// always resolved from the account of the token, never from an identifier in the request: a creator can only touch their own sheet. A suspended
/// creator can read but not write, and cannot lift the suspension by publishing again (only an administrator does).
/// </summary>
public static class StudioHandler
{
    public const string UnpublishReason = "creator_request";

    public static async Task<Result<StudioRegistrationDto>> Handle(GetRegistrationQuery query, ICreatorRepository creators, ICreatorTerms terms, CancellationToken cancellationToken) =>
        Result.Success(Registration(await creators.FindByAccountAsync(query.AccountId, cancellationToken), terms));

    /// <summary>
    /// Creates the profile (a draft) and records the acceptance of the creator terms in one step; <c>CreatorTermsAcceptedV1</c> then makes Platform
    /// grant the <c>creator</c> role. Repeating it is harmless: an account that is already a creator only gets its acceptance brought up to date.
    /// </summary>
    public static async Task<Result<StudioRegistrationDto>> Handle(StudioSignupCommand command, ICreatorRepository creators, ICreatorTerms terms, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (command.Request.AcceptedTermsVersion != terms.CurrentVersion)
        {
            return Result.Failure<StudioRegistrationDto>("terms_required", "Acceptez les CGU créateurs en vigueur pour ouvrir votre espace.");
        }

        var now = clock.GetUtcNow();
        if (await creators.FindByAccountAsync(command.AccountId, cancellationToken) is { } existing)
        {
            return await AcceptAsync(existing, terms.CurrentVersion, creators, unit, terms, now, cancellationToken);
        }

        var (profile, violation) = CreatorProfileRules.Normalize(new CreatorProfile(command.Request.Handle, command.Request.DisplayName, null, null, ["fr"], [], [], []));
        if (profile is null)
        {
            return Result.Failure<StudioRegistrationDto>(violation!);
        }

        if (await creators.FindByHandleAsync(profile.Handle, cancellationToken) is not null)
        {
            return await HandleTaken<StudioRegistrationDto>(profile.Handle, creators, cancellationToken);
        }

        var creator = Creator.NewDraft(Guid.CreateVersion7(), profile, founding: false, now).WithAccount(command.AccountId, now).WithTermsAccepted(terms.CurrentVersion, now, now);
        await creators.StageAsync(creator, cancellationToken);
        try
        {
            await unit.CommitAsync([CreatorEvents.TermsAccepted(creator, now)!], cancellationToken);
        }
        catch (UniqueConflictException)
        {
            return await HandleTaken<StudioRegistrationDto>(profile.Handle, creators, cancellationToken);
        }

        return Result.Success(Registration(creator, terms));
    }

    /// <summary>The creator accepts a newer version of the terms; the event is sent again so the role is there whatever happened before.</summary>
    public static async Task<Result<StudioRegistrationDto>> Handle(AcceptTermsCommand command, ICreatorRepository creators, ICreatorTerms terms, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (command.Version != terms.CurrentVersion)
        {
            return Result.Failure<StudioRegistrationDto>("terms_required", "Acceptez les CGU créateurs en vigueur.");
        }

        return await creators.FindByAccountAsync(command.AccountId, cancellationToken) is { } creator
            ? await AcceptAsync(creator, terms.CurrentVersion, creators, unit, terms, clock.GetUtcNow(), cancellationToken)
            : Result.Failure<StudioRegistrationDto>("creator_not_found", "Aucun espace créateur pour ce compte.");
    }

    public static async Task<Result<StudioProfileDto>> Handle(GetStudioProfileQuery query, ICreatorRepository creators, ICreatorQueries queries, CancellationToken cancellationToken) =>
        await creators.FindByAccountAsync(query.AccountId, cancellationToken) is { } creator ? await Profile(creator.Id, queries, cancellationToken) : NotFound<StudioProfileDto>();

    public static async Task<Result<StudioProfileDto>> Handle(UpdateStudioProfileCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<StudioProfileDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        // A published page keeps its handle: it is in shared links, in the app and in search engines.
        if (creator.Status == CreatorStatuses.Published && !string.Equals(command.Profile.Handle?.Trim().TrimStart('@'), creator.Handle, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<StudioProfileDto>("handle_locked", "Le handle d'un profil publié ne change pas : dépubliez-le d'abord.");
        }

        var updated = await CreatorAdminHandler.Handle(new UpdateCreatorCommand(command.AccountId, creator.Id, command.Profile), creators, queries, unit, clock, cancellationToken);
        return updated.IsSuccess ? await Profile(creator.Id, queries, cancellationToken) : Result.Failure<StudioProfileDto>(updated.Error!.Code, updated.Error.Message, updated.Error.Suggestion);
    }

    public static async Task<Result<StudioProfileDto>> Handle(PublishStudioCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<StudioProfileDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var published = await CreatorAdminHandler.Handle(new PublishCreatorCommand(command.AccountId, creator.Id), creators, queries, unit, clock, cancellationToken);
        return published.IsSuccess ? await Profile(creator.Id, queries, cancellationToken) : Result.Failure<StudioProfileDto>(published.Error!.Code, published.Error.Message);
    }

    public static async Task<Result<StudioProfileDto>> Handle(UnpublishStudioCommand command, ICreatorRepository creators, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<StudioProfileDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var unpublished = await CreatorAdminHandler.Handle(new UnpublishCreatorCommand(command.AccountId, creator.Id, UnpublishReason), creators, queries, unit, clock, cancellationToken);
        return unpublished.IsSuccess ? await Profile(creator.Id, queries, cancellationToken) : Result.Failure<StudioProfileDto>(unpublished.Error!.Code, unpublished.Error.Message);
    }

    public static async Task<Result<AdminContentDto>> Handle(StudioAddContentCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<AdminContentDto>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new AddContentCommand(command.AccountId, creator.Id, command.Content), creators, contents, unit, clock, cancellationToken);
    }

    public static async Task<Result<AdminContentDto>> Handle(StudioUpdateContentCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<AdminContentDto>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new UpdateContentCommand(command.AccountId, creator.Id, command.ContentId, command.Content), contents, unit, clock, cancellationToken);
    }

    public static async Task<Result<bool>> Handle(StudioRemoveContentCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<bool>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new RemoveContentCommand(command.AccountId, creator.Id, command.ContentId), contents, unit, clock, cancellationToken);
    }

    public static async Task<Result<AdminPlaceLinkDto>> Handle(StudioAddPlaceLinkCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<AdminPlaceLinkDto>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new AddPlaceLinkCommand(command.AccountId, creator.Id, command.Link), creators, contents, directory, unit, clock, cancellationToken);
    }

    public static async Task<Result<AdminPlaceLinkDto>> Handle(StudioSetPlaceLinkStatusCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<AdminPlaceLinkDto>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new SetPlaceLinkStatusCommand(command.AccountId, creator.Id, command.LinkId, command.Status), contents, directory, unit, clock, cancellationToken);
    }

    public static async Task<Result<bool>> Handle(StudioRemovePlaceLinkCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<bool>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new RemovePlaceLinkCommand(command.AccountId, creator.Id, command.LinkId), contents, unit, clock, cancellationToken);
    }

    public static async Task<Result<AdminTipDto>> Handle(StudioSetTipCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<AdminTipDto>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new SetTipCommand(command.AccountId, creator.Id, command.PoiId, command.Text), creators, contents, directory, unit, clock, cancellationToken);
    }

    public static async Task<Result<bool>> Handle(StudioRemoveTipCommand command, ICreatorRepository creators, IContentRepository contents, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await Own<bool>(command.AccountId, creators, cancellationToken);
        return creator is null ? failure! : await CreatorContentHandler.Handle(new RemoveTipCommand(command.AccountId, creator.Id, command.PoiId), contents, unit, clock, cancellationToken);
    }

    public static Task<Result<IReadOnlyList<PoiSearchResultDto>>> Handle(StudioSearchPlacesQuery query, IPoiDirectory directory, CancellationToken cancellationToken) =>
        CreatorContentHandler.Handle(new SearchPlacesQuery(query.Query, query.Destination, 20), directory, cancellationToken);

    /// <summary>The creator of the account, if it can write: it exists and is not suspended.</summary>
    internal static async Task<(Creator? Creator, Result<T>? Failure)> Own<T>(Guid accountId, ICreatorRepository creators, CancellationToken cancellationToken)
    {
        if (await creators.FindByAccountAsync(accountId, cancellationToken) is not { } creator)
        {
            return (null, NotFound<T>());
        }

        return creator.Status == CreatorStatuses.Suspended
            ? (null, Result.Failure<T>("creator_suspended", "Votre espace est suspendu : contactez l'équipe ON.VOYAGE."))
            : (creator, null);
    }

    internal static StudioProfileDto ToProfile(AdminCreatorDetailDto detail) =>
        new(
            detail.Id,
            detail.Handle,
            detail.DisplayName,
            detail.Bio,
            detail.AvatarPath,
            detail.Languages,
            detail.Specialties,
            detail.DestinationIds,
            detail.Links,
            detail.Status,
            detail.Founding,
            detail.TermsVersion,
            detail.TermsAcceptedAt,
            detail.FollowerCount >= FollowerDisplayThreshold ? detail.FollowerCount : null,
            detail.FollowerCount < FollowerDisplayThreshold,
            detail.PublishBlock,
            detail.Contents,
            detail.PlaceLinks,
            detail.Tips);

    /// <summary>Followers are shown from this many (F-26), here as on the public page.</summary>
    public const int FollowerDisplayThreshold = 20;

    private static async Task<Result<StudioRegistrationDto>> AcceptAsync(Creator creator, string version, ICreatorRepository creators, ICreatorsUnitOfWork unit, ICreatorTerms terms, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (creator.TermsVersion == version && creator.TermsAccepted)
        {
            return Result.Success(Registration(creator, terms));
        }

        var accepted = creator.WithTermsAccepted(version, now, now);
        await creators.StageAsync(accepted, cancellationToken);
        await unit.CommitAsync([CreatorEvents.TermsAccepted(accepted, now)!], cancellationToken);
        return Result.Success(Registration(accepted, terms));
    }

    private static StudioRegistrationDto Registration(Creator? creator, ICreatorTerms terms) =>
        creator is null
            ? new StudioRegistrationDto(false, terms.CurrentVersion, null, null, null, false)
            : new StudioRegistrationDto(true, terms.CurrentVersion, creator.Id, creator.Handle, creator.Status, creator.TermsAccepted && (creator.TermsVersion == terms.CurrentVersion || creator.TermsVersion == TermsVersions.Founder));

    private static async Task<Result<StudioProfileDto>> Profile(Guid creatorId, ICreatorQueries queries, CancellationToken cancellationToken) =>
        await queries.GetAdminDetailAsync(creatorId, cancellationToken) is { } detail ? Result.Success(ToProfile(detail)) : NotFound<StudioProfileDto>();

    private static Result<T> NotFound<T>() => Result.Failure<T>("creator_not_found", "Aucun espace créateur pour ce compte.");

    private static async Task<Result<T>> HandleTaken<T>(string handle, ICreatorRepository creators, CancellationToken cancellationToken)
    {
        var taken = await creators.HandlesStartingWithAsync(handle[..Math.Min(handle.Length, Handles.MaxLength - 4)], cancellationToken);
        var suggestion = Handles.Suggest(handle, taken.Contains);
        return Result.Failure<T>("handle_taken", $"Le handle @{handle} est déjà pris. Suggestion : @{suggestion}.", suggestion);
    }
}

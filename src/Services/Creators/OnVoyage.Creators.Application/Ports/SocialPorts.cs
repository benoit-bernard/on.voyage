using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Ports;

/// <summary>A content as a platform describes it. Nothing here is a token or a media: a reference, a text and the address of a thumbnail to copy.</summary>
public sealed record RemoteContent(
    string ExternalId,
    string Permalink,
    string Title,
    string? Caption,
    string Kind,
    DateTimeOffset? PublishedAt,
    int? DurationSeconds,
    string? ThumbnailUrl,
    IReadOnlyList<Chapter> Chapters);

public enum FetchStatus
{
    Ok,

    /// <summary>The platform no longer accepts the tokens: the creator has to connect again.</summary>
    NeedsReauth,

    /// <summary>The platform did not answer or refused for another reason: nothing is changed, the next run tries again.</summary>
    Unavailable,
}

/// <summary><c>Complete</c> is true when the list reached the end of the account's contents (so a stored content missing from it is gone from the platform).</summary>
public sealed record RemoteFetch(FetchStatus Status, IReadOnlyList<RemoteContent> Items, bool Complete, string? Error = null)
{
    public static RemoteFetch Failed(FetchStatus status, string error) => new(status, [], false, error);
}

public enum ConnectRefusal
{
    None,

    /// <summary>Instagram: a personal account (F-27 criterion).</summary>
    ProfessionalAccountRequired,

    /// <summary>The creator declined, or the platform gave no code.</summary>
    AccessDenied,

    /// <summary>The state is unknown, expired, for another platform or for another creator.</summary>
    InvalidState,

    /// <summary>This platform account is already connected to another creator.</summary>
    AccountInUse,

    /// <summary>The platform did not accept the exchange or did not answer.</summary>
    ProviderError,

    /// <summary>The imports of this platform are not open (application review pending, no credentials).</summary>
    Disabled,
}

public sealed record ConnectOutcome(ConnectRefusal Refusal, ConnectedAccount? Account);

/// <summary>
/// OAuth connections and imports (F-27). The implementation is the only code that sees an access token: it encrypts them with Data Protection,
/// refreshes them, calls the platform and revokes them. The application only deals with accounts and contents.
/// </summary>
public interface ISocialConnector
{
    bool IsEnabled(string platform);

    /// <summary>The address to send the creator to (code + PKCE; the state binds the creator, the platform and a short lifetime).</summary>
    Uri Begin(string platform, Guid creatorId);

    /// <summary>Checks the state, exchanges the code, reads the profile, refuses what F-27 refuses, and stores the account with its tokens (encrypted).</summary>
    Task<ConnectOutcome> CompleteAsync(string platform, Guid creatorId, string code, string state, CancellationToken cancellationToken);

    /// <summary>The creator's most recent contents (renewing the access token when it is about to expire).</summary>
    Task<RemoteFetch> FetchAsync(Guid connectedAccountId, int limit, CancellationToken cancellationToken);

    /// <summary>Revokes the tokens at the platform when it has a way to (best effort). The tokens are deleted by <see cref="IConnectedAccountRepository.DeleteAsync"/>.</summary>
    Task RevokeAsync(Guid connectedAccountId, CancellationToken cancellationToken);
}

/// <summary>Connected accounts without their tokens.</summary>
public interface IConnectedAccountRepository
{
    Task<IReadOnlyList<ConnectedAccount>> ListAsync(Guid creatorId, CancellationToken cancellationToken);

    Task<ConnectedAccount?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ConnectedAccount?> FindAsync(Guid creatorId, string platform, CancellationToken cancellationToken);

    /// <summary>Active accounts never synchronised, or last synchronised before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountContentsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    Task StageAsync(ConnectedAccount account, CancellationToken cancellationToken);

    /// <summary>Stages the deletion of the account and of its tokens; its contents keep existing (detached) unless they are removed by the caller.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Copies the thumbnail of a content under the licence of the creator terms (F-27) and deletes it with the content or the connection.</summary>
public interface IThumbnailStore
{
    /// <summary>The cover path (relative to the media root) of the stored copy, or null when the address is not acceptable or the copy failed.</summary>
    Task<string?> SaveAsync(Guid creatorId, Guid contentId, string sourceUrl, CancellationToken cancellationToken);

    Task DeleteAsync(string coverPath, CancellationToken cancellationToken);
}

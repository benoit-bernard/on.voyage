namespace OnVoyage.Creators.Domain;

public static class ConnectionStatuses
{
    /// <summary>Tokens are valid: imports run.</summary>
    public const string Active = "active";

    /// <summary>The platform refused the refresh (access revoked, password changed…): the creator has to connect again; the tokens are gone.</summary>
    public const string NeedsReauth = "needs_reauth";
}

/// <summary>
/// A social account that a creator connected to prove they own it and to import its contents (F-27). This is the whole domain view of it:
/// the OAuth tokens are never part of it. They are encrypted in the table and never leave the Creators infrastructure (§23.2).
/// </summary>
public sealed record ConnectedAccount(
    Guid Id,
    Guid CreatorId,
    string Platform,
    string ExternalUserId,
    string Username,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? LastSyncAt,
    string Status,
    string? LastError,
    DateTimeOffset CreatedAt)
{
    public bool IsActive => Status == ConnectionStatuses.Active;

    public ConnectedAccount Synced(DateTimeOffset now) => this with { LastSyncAt = now, LastError = null };

    public ConnectedAccount NeedingReauth(string reason) => this with { Status = ConnectionStatuses.NeedsReauth, LastError = reason, ExpiresAt = null };

    public ConnectedAccount Failed(string reason) => this with { LastError = reason.Length > 200 ? reason[..200] : reason };
}

/// <summary>The platforms a creator can connect (the third, TikTok, is V1.1 and refused here).</summary>
public static class ConnectablePlatforms
{
    public static IReadOnlyList<string> All { get; } = [ContentPlatforms.Instagram, ContentPlatforms.YouTube];

    public static bool IsKnown(string? platform) => platform is ContentPlatforms.Instagram or ContentPlatforms.YouTube;
}

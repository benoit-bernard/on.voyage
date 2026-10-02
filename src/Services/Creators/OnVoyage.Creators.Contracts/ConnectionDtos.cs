namespace OnVoyage.Creators.Contracts;

// ---- Connected accounts (F-27, T-1207, T-1208). Never carries a token: they stay encrypted inside the Creators service. ----

/// <summary>A social account the creator connected. <c>Status</c> is <c>active</c> or <c>needs_reauth</c> (the platform refused the renewal: connect again).</summary>
public sealed record ConnectedAccountDto(Guid Id, string Platform, string Username, string Status, DateTimeOffset? LastSyncAt, int ContentCount, string? LastError, IReadOnlyList<string> Scopes);

/// <summary>One platform of the connections screen. <c>Enabled</c> is false while the platform's application review is pending (H-008): the screen then offers the URL alternative.</summary>
public sealed record ConnectionPlatformDto(string Platform, bool Enabled, ConnectedAccountDto? Account);

public sealed record ConnectionsDto(IReadOnlyList<ConnectionPlatformDto> Platforms);

/// <summary>Where to send the creator's browser to authorize ON.VOYAGE on the platform.</summary>
public sealed record ConnectionStartDto(string AuthorizeUrl);

/// <summary>The <c>code</c> and <c>state</c> that the platform put in the return address; the state is only valid for the creator who asked for it.</summary>
public sealed record CompleteConnectionRequest(string Code, string State);

/// <summary>What a resynchronisation did, per platform: contents created, updated, and withdrawn because they disappeared from the platform.</summary>
public sealed record SyncReportDto(int Created, int Updated, int Removed, string Status);

public sealed record SyncRequestedDto(int Accounts);

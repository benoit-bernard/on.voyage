namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class RemoteConfigRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = "{}";
    public int Version { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RemoteConfigHistoryRow
{
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Value { get; set; } = "{}";
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class FeatureFlagRow
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public short RolloutPercent { get; set; }
    public string[] Platforms { get; set; } = [];
    public string? MinAppVersion { get; set; }
}

internal sealed class ConsentRow
{
    public Guid TravelerId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public bool Granted { get; set; }
    public string TextVersion { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AccountRow
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string[] Roles { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public Guid? ReplacedBy { get; set; }
}

internal sealed class OtpChallengeRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

internal sealed class RefreshTokenRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class AdminAuditRow
{
    public Guid EventId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? Summary { get; set; }
}

internal sealed class DeletionRequestRow
{
    public Guid TravelerId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string[] RequiredServices { get; set; } = [];
}

internal sealed class DeletionAckRow
{
    public Guid TravelerId { get; set; }
    public string Service { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

/// <summary>Proof that a deletion happened, without saying whose: no traveler identifier is kept (F-22).</summary>
internal sealed class DeletionLogRow
{
    public long Id { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int ServiceCount { get; set; }
}

internal sealed class ExportRequestRow
{
    public Guid Id { get; set; }
    public Guid TravelerId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string[] RequiredServices { get; set; } = [];
}

internal sealed class ExportPartRow
{
    public Guid ExportId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

namespace OnVoyage.Platform.Domain;

/// <summary>
/// A traveler account. The id is the <c>traveler_id</c> used by every service. It starts anonymous and keeps the same id when an
/// e-mail is verified and linked (F-01).
/// </summary>
public sealed record Account(
    Guid Id,
    string? Email,
    DateTimeOffset? EmailVerifiedAt,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    Guid? ReplacedBy)
{
    public bool IsAnonymous => EmailVerifiedAt is null;

    public static Account NewAnonymous(Guid id, DateTimeOffset now) => new(id, null, null, [], now, now, null);

    public Account LinkEmail(string email, DateTimeOffset now) => this with { Email = email, EmailVerifiedAt = now, LastActiveAt = now };

    public Account WithRole(string role) => Roles.Contains(role) ? this : this with { Roles = [.. Roles, role] };
}

public sealed record OtpChallenge(Guid Id, string Email, string CodeHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Attempts, DateTimeOffset? ConsumedAt);

public sealed record RefreshTokenRecord(Guid Id, Guid AccountId, Guid FamilyId, string TokenHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt);

public sealed record AuthSettings(
    TimeSpan OtpLifetime,
    int OtpMaxAttempts,
    TimeSpan OtpResendCooldown,
    int OtpPerEmailPerHour,
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime,
    IReadOnlyList<string> BootstrapAdminEmails);

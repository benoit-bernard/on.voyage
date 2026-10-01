namespace OnVoyage.Platform.Contracts;

// Public DTOs of the data rights of F-22 (export and deletion), orchestrated by Platform (T-507, §13).

/// <summary><c>Status</c> is <c>pending</c> while services are still answering, <c>ready</c> once the archive can be downloaded, <c>expired</c> after 24 hours.</summary>
public sealed record ExportStatusDto(Guid ExportId, string Status, DateTimeOffset RequestedAt, DateTimeOffset? ExpiresAt, IReadOnlyList<string> PendingServices);

/// <summary><c>Status</c> is <c>pending</c> until every required service acknowledged, then <c>completed</c> (the account no longer exists).</summary>
public sealed record DeletionStatusDto(string Status, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, IReadOnlyList<string> PendingServices);

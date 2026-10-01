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

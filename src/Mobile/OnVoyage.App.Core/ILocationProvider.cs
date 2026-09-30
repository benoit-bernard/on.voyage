namespace OnVoyage.App.Core;

public readonly record struct Position(double Latitude, double Longitude);

/// <summary>Foreground position (MVP-0). The position is used in-memory for ranking and in query parameters; never persisted.</summary>
public interface ILocationProvider
{
    Task<Position?> GetCurrentAsync(CancellationToken cancellationToken);
}

public sealed class NoLocationProvider : ILocationProvider
{
    public Task<Position?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<Position?>(null);
}

namespace OnVoyage.App.Core.Profile;

public interface IProfileStore
{
    Task<LocalProfile> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(LocalProfile profile, CancellationToken cancellationToken);
}

public sealed class InMemoryProfileStore : IProfileStore
{
    private LocalProfile _profile = new();

    public Task<LocalProfile> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

    public Task SaveAsync(LocalProfile profile, CancellationToken cancellationToken)
    {
        _profile = profile;
        return Task.CompletedTask;
    }
}

using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Search;

public enum SearchStatus
{
    /// <summary>Nothing typed.</summary>
    Idle,

    /// <summary>Typed, but under two characters: no request is made.</summary>
    TooShort,

    Searching,

    Results,

    /// <summary>Searched, nothing matches.</summary>
    Empty,

    /// <summary>No network and no pack to search in.</summary>
    Failed,
}

/// <summary>What the search screen shows. <paramref name="FromPack"/> is true when the answer came from the installed pack (offline).</summary>
public sealed record SearchState(string Text, SearchStatus Status, IReadOnlyList<PoiSummaryDto> Results, bool FromPack = false)
{
    public static SearchState Initial { get; } = new(string.Empty, SearchStatus.Idle, []);
}

/// <summary>
/// Search in the pack of the destination (SQLite FTS5) when there is no network (F-14, F-15). Returns null when no pack is installed for the
/// destination, so the caller reports the failure instead of an empty list.
/// </summary>
public interface IOfflineSearch
{
    Task<IReadOnlyList<PoiSummaryDto>?> SearchAsync(string destination, string text, int limit, CancellationToken cancellationToken);
}

public sealed class NoOfflineSearch : IOfflineSearch
{
    public Task<IReadOnlyList<PoiSummaryDto>?> SearchAsync(string destination, string text, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PoiSummaryDto>?>(null);
}

/// <summary>
/// The search field (F-14): debounce of 300 ms, at least two characters, the Catalog first and the pack when the network fails. Only the latest
/// typing counts: an answer that arrives after a newer keystroke is dropped. The typed text goes to the Catalog as the <c>q</c> parameter and
/// nowhere else; the statistics only get how many results there were.
/// </summary>
public sealed class SearchSession(ICatalogClient catalog, IOfflineSearch offline, IProfileStore profiles, IAnalyticsSink analytics, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    public const int MinLength = 2;
    public const int ResultLimit = 20;

    private CancellationTokenSource? _typing;
    private int _generation;

    public event Action? Changed;

    public SearchState State { get; private set; } = SearchState.Initial;

    /// <summary>The last search being run; tests await it, the screen never needs to.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        _typing?.Cancel();
        _typing?.Dispose();
    }

    /// <summary>Called on each keystroke: waits for a pause of <see cref="Debounce"/> before searching.</summary>
    public void OnInput(string? typed)
    {
        var text = Normalize(typed);
        var generation = Restart(out var token);
        if (text.Length == 0)
        {
            Publish(SearchState.Initial);
            return;
        }

        if (text.Length < MinLength)
        {
            Publish(new SearchState(text, SearchStatus.TooShort, []));
            return;
        }

        Pending = DebouncedAsync(text, generation, token);
    }

    /// <summary>The Enter key: searches now, without waiting for the pause.</summary>
    public Task SearchNowAsync(string? typed)
    {
        var text = Normalize(typed);
        if (text.Length < MinLength)
        {
            OnInput(typed);
            return Task.CompletedTask;
        }

        var generation = Restart(out var token);
        return Pending = RunAsync(text, generation, token);
    }

    public static string Normalize(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private int Restart(out CancellationToken token)
    {
        _typing?.Cancel();
        _typing?.Dispose();
        _typing = new CancellationTokenSource();
        token = _typing.Token;
        return ++_generation;
    }

    private async Task DebouncedAsync(string text, int generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(Debounce, clock, token);
        }
        catch (OperationCanceledException)
        {
            return; // a newer keystroke replaced this one
        }

        await RunAsync(text, generation, token);
    }

    private async Task RunAsync(string text, int generation, CancellationToken token)
    {
        Publish(State with { Text = text, Status = SearchStatus.Searching });
        SearchState outcome;
        try
        {
            var destination = (await profiles.LoadAsync(token)).Destination;
            outcome = await SearchAsync(destination, text, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return; // typed again while waiting
        }

        analytics.Track("search_performed", new Dictionary<string, object?> { ["results_count"] = outcome.Results.Count });
        Publish(outcome);
    }

    private async Task<SearchState> SearchAsync(string destination, string text, CancellationToken token)
    {
        try
        {
            var online = await catalog.SearchAsync(destination, text, ResultLimit, token);
            return Done(text, online, fromPack: false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !token.IsCancellationRequested))
        {
            // No network (or the request timed out, which the HTTP client reports as a cancellation of its own).
            var local = await offline.SearchAsync(destination, text, ResultLimit, token);
            return local is null ? new SearchState(text, SearchStatus.Failed, []) : Done(text, local, fromPack: true);
        }
    }

    private static SearchState Done(string text, IReadOnlyList<PoiSummaryDto> results, bool fromPack) =>
        new(text, results.Count == 0 ? SearchStatus.Empty : SearchStatus.Results, results, fromPack);

    private void Publish(SearchState state)
    {
        State = state;
        Changed?.Invoke();
    }
}

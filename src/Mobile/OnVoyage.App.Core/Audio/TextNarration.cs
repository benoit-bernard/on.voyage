using System.Text.RegularExpressions;

namespace OnVoyage.App.Core.Audio;

/// <summary>What the on-device narrator reports while it reads a story aloud.</summary>
public abstract record NarrationEvent
{
    /// <summary><paramref name="Fraction"/> of the text already read, 0 to 1.</summary>
    public sealed record Progress(double Fraction) : NarrationEvent;

    public sealed record Ended : NarrationEvent;

    public sealed record Failed(string Message) : NarrationEvent;
}

/// <summary>
/// Reads the text of a story with the voice of the device, for the stories published without audio (no TTS voice was available when they
/// were produced). The text is the story's own text: nothing is sent anywhere. Where the platform has no voice, <see cref="IsAvailable"/> is false.
/// </summary>
public interface ITextNarrator
{
    event Action<NarrationEvent>? Event;

    bool IsAvailable { get; }

    /// <summary>False where the platform voice cannot change its rate: <see cref="SetSpeedAsync"/> then has no effect.</summary>
    bool SupportsSpeed { get; }

    /// <summary>Starts reading from the beginning, replacing what was being read. Returns once reading has begun.</summary>
    Task SpeakAsync(string text, string language, double speed, CancellationToken cancellationToken);

    Task PauseAsync();

    Task ResumeAsync();

    Task SetSpeedAsync(double speed);

    Task StopAsync();
}

public sealed class NullTextNarrator : ITextNarrator
{
    public event Action<NarrationEvent>? Event
    {
        add { }
        remove { }
    }

    public bool IsAvailable => false;

    public bool SupportsSpeed => false;

    public Task SpeakAsync(string text, string language, double speed, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task SetSpeedAsync(double speed) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>The platform's speech synthesis: reads one short piece of text and says whether it was read to the end.</summary>
public interface ISpeechEngine
{
    bool IsAvailable { get; }

    bool SupportsRate { get; }

    /// <summary>Reads <paramref name="text"/> in <paramref name="language"/> (two letters, "fr") at <paramref name="rate"/> (1 = normal). True when it ended, false when it was cancelled.</summary>
    Task<bool> SpeakAsync(string text, string language, double rate, CancellationToken cancellationToken);
}

/// <summary>
/// Cuts a story into pieces a speech engine reads reliably (browsers stop reading long texts, and a piece is what pause and speed changes restart).
/// Cuts fall on sentence ends; a sentence longer than the limit is cut at a comma or a space.
/// </summary>
public static partial class NarrationChunker
{
    public const int MaxChars = 240;

    public static IReadOnlyList<string> Split(string text, int maxChars = MaxChars)
    {
        List<string> chunks = [];
        var current = new System.Text.StringBuilder();
        foreach (var sentence in SentenceEnd().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            foreach (var piece in Cut(sentence, maxChars))
            {
                if (current.Length > 0 && current.Length + 1 + piece.Length > maxChars)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                current.Append(current.Length > 0 ? " " : string.Empty).Append(piece);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static IEnumerable<string> Cut(string sentence, int maxChars)
    {
        var rest = sentence;
        while (rest.Length > maxChars)
        {
            var at = rest.LastIndexOf(',', maxChars - 1);
            at = at > maxChars / 3 ? at + 1 : rest.LastIndexOf(' ', maxChars - 1);
            at = at > 0 ? at : maxChars;
            yield return rest[..at].Trim();
            rest = rest[at..].Trim();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+|\n+")]
    private static partial Regex SentenceEnd();
}

/// <summary>
/// An <see cref="ITextNarrator"/> over any <see cref="ISpeechEngine"/>: the story is read piece by piece, which gives pause, resume, a speed
/// change (the current piece restarts at the new rate) and a progress, whatever the platform offers. All the logic is here so it is tested once.
/// </summary>
public sealed class SequentialTextNarrator(ISpeechEngine engine) : ITextNarrator
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _chunks = [];
    private int[] _cumulative = [];
    private int _index;
    private string _language = "fr";
    private double _speed = 1d;
    private bool _paused;
    private bool _active;
    private int _generation;
    private CancellationTokenSource? _current;

    public event Action<NarrationEvent>? Event;

    public bool IsAvailable => engine.IsAvailable;

    public bool SupportsSpeed => engine.SupportsRate;

    public Task SpeakAsync(string text, string language, double speed, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Cancel();
            _chunks = NarrationChunker.Split(text);
            _cumulative = Prefix(_chunks);
            _index = 0;
            _language = language;
            _speed = speed;
            _paused = false;
            _active = _chunks.Count > 0;
            if (_active)
            {
                Run();
            }
        }

        if (!_active)
        {
            Event?.Invoke(new NarrationEvent.Ended());
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_gate)
        {
            if (_active && !_paused)
            {
                _paused = true;
                Cancel(); // the current piece is read again from its start on resume
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        lock (_gate)
        {
            if (_active && _paused)
            {
                _paused = false;
                Run();
            }
        }

        return Task.CompletedTask;
    }

    public Task SetSpeedAsync(double speed)
    {
        lock (_gate)
        {
            _speed = speed;
            if (_active && !_paused && engine.SupportsRate)
            {
                Cancel();
                Run();
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            Cancel();
            _active = false;
            _paused = false;
            _chunks = [];
        }

        return Task.CompletedTask;
    }

    /// <summary>Characters read once piece <c>i</c> is done: the progress is measured in text, not in pieces.</summary>
    private static int[] Prefix(IReadOnlyList<string> chunks)
    {
        var sums = new int[chunks.Count];
        var total = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            total += chunks[i].Length;
            sums[i] = total;
        }

        return sums;
    }

    private void Cancel()
    {
        _generation++;
        _current?.Cancel();
        _current = null;
    }

    /// <summary>Reads from the current piece on a background task; a newer generation (pause, speed, stop) retires this one.</summary>
    private void Run()
    {
        var generation = ++_generation;
        var source = new CancellationTokenSource();
        _current = source;
        _ = Task.Run(() => ReadAsync(generation, source.Token));
    }

    private async Task ReadAsync(int generation, CancellationToken token)
    {
        try
        {
            while (true)
            {
                string chunk;
                string language;
                double speed;
                lock (_gate)
                {
                    if (generation != _generation || _index >= _chunks.Count)
                    {
                        break;
                    }

                    (chunk, language, speed) = (_chunks[_index], _language, _speed);
                }

                if (!await engine.SpeakAsync(chunk, language, speed, token))
                {
                    return; // cancelled: whoever cancelled decides what happens next
                }

                double fraction;
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    _index++;
                    fraction = _cumulative[^1] == 0 ? 1d : (double)_cumulative[_index - 1] / _cumulative[^1];
                }

                Event?.Invoke(new NarrationEvent.Progress(fraction));
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _active = false;
            }

            Event?.Invoke(new NarrationEvent.Ended());
        }
        catch (OperationCanceledException)
        {
            // retired by a pause, a speed change or a stop
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _active = false;
            }

            Event?.Invoke(new NarrationEvent.Failed(exception.Message));
        }
    }
}

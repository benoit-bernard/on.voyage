using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Interactions;

/// <summary>
/// Sends a batch and applies the answer: the server replays the whole history in event order, so its vector replaces the local copy
/// (§6.4: the server is the reference). Used by both outboxes.
/// </summary>
public sealed class InteractionSender(IDiscoveryClient client, IProfileStore profiles)
{
    /// <returns>True when the server took the batch; false when it could not be reached.</returns>
    public async Task<bool> SendAsync(IReadOnlyList<InteractionDto> batch, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.PostInteractionsAsync(batch, cancellationToken);
            await ApplyAsync(response, cancellationToken);
            return true;
        }
        catch (HttpRequestException ex) when (IsPermanent(ex.StatusCode))
        {
            // A batch the server will never accept (validation) must not block the ones behind it.
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static bool IsPermanent(System.Net.HttpStatusCode? status) =>
        status is { } code && (int)code is >= 400 and < 500 && code is not (System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);

    public async Task ApplyAsync(InteractionBatchResponse response, CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        await profiles.SaveAsync(
            profile with
            {
                Affinities = new Dictionary<string, double>(response.Vector),
                Depth = Math.Max(profile.Depth, response.ProfileDepth),
                Excluded = [.. profile.Excluded.Concat(response.Excluded)],
            },
            cancellationToken);
    }
}

/// <summary>PWA outbox: interactions are sent immediately; the ones that fail wait in memory for the next call.</summary>
public sealed class DirectInteractionOutbox(InteractionSender sender) : IInteractionOutbox, IDisposable
{
    private readonly List<InteractionDto> _waiting = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _waiting.Add(interaction);
        }
        finally
        {
            _gate.Release();
        }

        await FlushAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (_waiting.Count > 0)
            {
                var batch = _waiting.Take(IngestMax).ToArray();
                if (!await sender.SendAsync(batch, cancellationToken))
                {
                    return;
                }

                _waiting.RemoveRange(0, batch.Length);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private const int IngestMax = 200;
}

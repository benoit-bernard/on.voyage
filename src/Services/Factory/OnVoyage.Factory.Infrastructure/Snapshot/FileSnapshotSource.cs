using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OnVoyage.Factory.Application.Features.Snapshot;

namespace OnVoyage.Factory.Infrastructure.Snapshot;

/// <summary>
/// Reads <c>data-pipeline/&lt;destination&gt;/destination.json</c> and <c>pois.json</c>. The root is <c>Factory:Snapshot:Directory</c> (the
/// <c>data-pipeline</c> folder); when unset, the folder is looked for in the current directory and above, so <c>dotnet run</c> from a checkout works.
/// </summary>
internal sealed class FileSnapshotSource(IConfiguration configuration) : ISnapshotSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public async Task<SnapshotBundle?> LoadAsync(string destinationSlug, CancellationToken cancellationToken)
    {
        if (destinationSlug.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            return null; // the slug becomes a folder name
        }

        var folder = Locate(destinationSlug);
        if (folder is null)
        {
            return null;
        }

        await using var destinationFile = File.OpenRead(Path.Combine(folder, "destination.json"));
        var destination = await JsonSerializer.DeserializeAsync<SnapshotDestination>(destinationFile, Options, cancellationToken)
            ?? throw new InvalidDataException("destination.json is empty.");
        await using var poisFile = File.OpenRead(Path.Combine(folder, "pois.json"));
        var pois = await JsonSerializer.DeserializeAsync<List<SnapshotPoi>>(poisFile, Options, cancellationToken)
            ?? throw new InvalidDataException("pois.json is empty.");
        return new SnapshotBundle(destination, pois);
    }

    private string? Locate(string destinationSlug)
    {
        if (configuration["Factory:Snapshot:Directory"] is { Length: > 0 } configured)
        {
            var candidate = Path.Combine(Path.GetFullPath(configured), destinationSlug);
            return File.Exists(Path.Combine(candidate, "destination.json")) ? candidate : null;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "data-pipeline", destinationSlug);
                if (File.Exists(Path.Combine(candidate, "destination.json")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

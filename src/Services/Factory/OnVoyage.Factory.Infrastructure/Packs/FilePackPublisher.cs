using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Features.Packs;
using OnVoyage.Packs;
using OnVoyage.Packs.Builder;

namespace OnVoyage.Factory.Infrastructure.Packs;

/// <summary>Where Factory writes files that Catalog serves: <c>Factory:MediaDirectory</c>, the same folder as the story audio.</summary>
internal static class MediaDirectory
{
    public static string Root(IConfiguration configuration) =>
        Path.GetFullPath(configuration["Factory:MediaDirectory"] ?? Path.Combine(configuration["Factory:DataDirectory"] ?? Path.Combine(Path.GetTempPath(), "onvoyage-factory"), "media"));
}

/// <summary>
/// Publishes a pack under <c>packs/{destination}/{lang}/</c> of the media directory: the archive <c>pack_{destination}_{lang}_v{n}.zip</c> and
/// <c>latest.json</c> (a <see cref="PackInfo"/>). Catalog serves the folder with HTTP Range through the Gateway (<c>/media/packs/…</c>), which is
/// what the app's resumable download needs. In MVP-0 the files are public like the audio; Billing's signed URLs (T-701) replace this. The previous
/// version stays one more publication so a download in progress can finish; older archives are deleted.
/// </summary>
internal sealed class FilePackPublisher(IConfiguration configuration, TimeProvider clock, ILogger<FilePackPublisher> logger) : IPackPublisher
{
    private static readonly JsonSerializerOptions Json = PackManifests.Json;

    private string Root => MediaDirectory.Root(configuration);

    public async Task<PackInfo?> LatestAsync(string destinationSlug, string lang, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, PackNames.LatestIndex(destinationSlug, lang));
        return File.Exists(path) ? JsonSerializer.Deserialize<PackInfo>(await File.ReadAllTextAsync(path, cancellationToken), Json) : null;
    }

    public async Task<PackInfo> PublishAsync(PackContent content, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), $"onvoyage-pack-{Guid.NewGuid():N}");
        try
        {
            var absolute = content with { Places = [.. content.Places.Select(place => place with { Stories = [.. place.Stories.Select(story => story with { Audio = [.. story.Audio.Select(audio => audio with { SourcePath = ResolveAudio(audio.SourcePath) })] })] })] };
            var manifest = await PackBuilder.BuildDirectoryAsync(absolute, Path.Combine(work, "pack"), clock.GetUtcNow(), cancellationToken);

            var directory = Path.Combine(Root, PackNames.Directory(content.Destination, content.Lang));
            var archiveName = PackNames.Archive(content.Destination, content.Lang, content.Version);
            var archivePath = Path.Combine(directory, archiveName);
            var info = await PackBuilder.ArchiveAsync(Path.Combine(work, "pack"), archivePath + ".tmp", manifest, $"{PackNames.Directory(content.Destination, content.Lang)}/{archiveName}", cancellationToken);
            File.Move(archivePath + ".tmp", archivePath, overwrite: true);

            var index = Path.Combine(directory, "latest.json");
            await File.WriteAllTextAsync(index + ".tmp", JsonSerializer.Serialize(info, Json), cancellationToken);
            File.Move(index + ".tmp", index, overwrite: true);

            Prune(directory, content.Version);
            logger.LogInformation("Pack {Destination}/{Lang} v{Version} published: {Size} bytes, {Places} places", content.Destination, content.Lang, content.Version, info.ArchiveSize, content.Places.Count);
            return info;
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            // The extract was made for this build in the temp folder; a map file anywhere else is not ours to delete.
            if (content.MapPath is { } map && Path.GetFullPath(map).StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && File.Exists(map))
            {
                File.Delete(map);
            }
        }
    }

    /// <summary>Audio paths are stored relative to the media directory; one that leaves it is a bug or an attack, not a file.</summary>
    private string ResolveAudio(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new InvalidOperationException("An audio path leaves the media directory.");
    }

    private static void Prune(string directory, int version)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "pack_*_v*.zip"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var marker = name.LastIndexOf("_v", StringComparison.Ordinal);
            if (marker >= 0 && int.TryParse(name[(marker + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var other) && other < version - 1)
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>
/// Cuts the destination's extract from a PMTiles source with the <c>pmtiles</c> command line tool of go-pmtiles
/// (<c>pmtiles extract SOURCE OUTPUT --bbox=minLon,minLat,maxLon,maxLat --maxzoom=15</c>), when <c>Factory:Pmtiles:Binary</c> and
/// <c>Factory:Pmtiles:Source</c> are set. The binary is not part of the worker image yet (see docs/questions); without it the pack has no map.
/// </summary>
internal sealed class PmtilesCliMapExtractor(IConfiguration configuration, ILogger<PmtilesCliMapExtractor> logger) : IMapExtractor
{
    public const int MaxZoom = 15;

    public async Task<string?> ExtractAsync(DestinationConfig destination, CancellationToken cancellationToken)
    {
        var binary = configuration["Factory:Pmtiles:Binary"];
        var source = configuration["Factory:Pmtiles:Source"];
        if (string.IsNullOrWhiteSpace(binary) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var output = Path.Combine(Path.GetTempPath(), $"onvoyage-map-{Guid.NewGuid():N}.pmtiles");
        var start = new ProcessStartInfo(binary) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in Arguments(source, output, destination))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The pmtiles tool could not be started.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            logger.LogWarning("pmtiles extract failed for {Destination}: {Error}", destination.Slug, error);
            return null; // a pack without a map is better than no pack
        }

        return output;
    }

    /// <summary>The argument list (never a shell string): the source and the box come from configuration and the destination table.</summary>
    internal static IReadOnlyList<string> Arguments(string source, string output, DestinationConfig destination) =>
    [
        "extract",
        source,
        output,
        string.Create(CultureInfo.InvariantCulture, $"--bbox={destination.MinLongitude},{destination.MinLatitude},{destination.MaxLongitude},{destination.MaxLatitude}"),
        string.Create(CultureInfo.InvariantCulture, $"--maxzoom={MaxZoom}"),
    ];
}

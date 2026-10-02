using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnVoyage.Packs;

public sealed record PackFile(string Path, long Size, string Sha256);

/// <summary><c>manifest.json</c> of §14.6. <see cref="CreatedAt"/> is the only value that changes when the same content is built again.</summary>
public sealed record PackManifest(
    string Destination,
    string Lang,
    int Version,
    [property: JsonPropertyName("taxonomyVersion")] string? TaxonomyVersion,
    string? MinAppVersion,
    IReadOnlyList<PackFile> Files,
    long TotalSize,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// What a published pack looks like from outside (<c>latest.json</c> next to the archive): enough to show its size before the download, to
/// know whether an update exists, and to check the archive once downloaded. <see cref="ArchivePath"/> is relative to the media root.
/// </summary>
public sealed record PackInfo(
    string Destination,
    string Lang,
    int Version,
    string ArchivePath,
    long ArchiveSize,
    string ArchiveSha256,
    long InstalledSize,
    string? MinAppVersion,
    DateTimeOffset CreatedAt);

public sealed class PackIntegrityException(string message) : Exception(message);

public static class PackNames
{
    /// <summary><c>pack_{destination}_{lang}_v{version}.zip</c>.</summary>
    public static string Archive(string destination, string lang, int version) => $"pack_{destination}_{lang}_v{version}.zip";

    /// <summary>Where the published archive and its index live, relative to the media root.</summary>
    public static string Directory(string destination, string lang) => $"packs/{destination}/{lang}";

    public static string LatestIndex(string destination, string lang) => $"{Directory(destination, lang)}/latest.json";
}

/// <summary>Reading, writing and checking <c>manifest.json</c>. The checks are what makes a downloaded or extracted pack trustworthy.</summary>
public static class PackManifests
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<PackManifest> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            throw new PackIntegrityException("manifest.json is missing.");
        }

        PackManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(path, cancellationToken), Json)
                ?? throw new PackIntegrityException("manifest.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new PackIntegrityException($"manifest.json is not valid: {ex.Message}");
        }

        return manifest;
    }

    /// <summary>Checks size and SHA-256 of every file listed, and that none leaves the directory. A pack without <c>pack.db</c> is refused.</summary>
    public static async Task VerifyAsync(string directory, PackManifest manifest, CancellationToken cancellationToken = default)
    {
        if (!manifest.Files.Any(f => f.Path == "pack.db"))
        {
            throw new PackIntegrityException("The manifest does not list pack.db.");
        }

        var root = System.IO.Path.GetFullPath(directory) + System.IO.Path.DirectorySeparatorChar;
        foreach (var file in manifest.Files)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file.Path));
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                throw new PackIntegrityException($"{file.Path} leaves the pack directory.");
            }

            if (!File.Exists(full) || new FileInfo(full).Length != file.Size)
            {
                throw new PackIntegrityException($"{file.Path} is missing or has the wrong size.");
            }

            if (!string.Equals(await HashFileAsync(full, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackIntegrityException($"{file.Path} does not match its SHA-256.");
            }
        }
    }

    /// <summary>Lowercase hexadecimal SHA-256 of a file.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>Writes <c>manifest.json</c> listing every other file of the directory, sorted, with size and hash.</summary>
    public static async Task<PackManifest> WriteAsync(
        string directory, string destination, string lang, int version, string? taxonomyVersion = null, string? minAppVersion = null, DateTimeOffset? createdAt = null, CancellationToken cancellationToken = default)
    {
        List<PackFile> files = [];
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative == "manifest.json")
            {
                continue;
            }

            files.Add(new PackFile(relative, new FileInfo(path).Length, await HashFileAsync(path, cancellationToken)));
        }

        var manifest = new PackManifest(destination, lang, version, taxonomyVersion, minAppVersion, files, files.Sum(f => f.Size), createdAt);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json), new System.Text.UTF8Encoding(false), cancellationToken);
        return manifest;
    }
}

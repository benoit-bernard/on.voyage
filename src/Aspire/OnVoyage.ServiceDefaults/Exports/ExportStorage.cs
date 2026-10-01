using System.Text;
using Microsoft.Extensions.Configuration;

namespace OnVoyage.ServiceDefaults.Exports;

/// <summary>
/// The private <c>exports/</c> area of §13: each service writes its part of a traveler's data export here and tells Platform the path; Platform
/// reads the parts, assembles the archive and removes them. A local directory in MVP-0 (<c>Exports:Directory</c>, shared by the services);
/// object storage later, behind the same two operations.
/// </summary>
public sealed class ExportStorage(IConfiguration configuration)
{
    private string Root => Path.GetFullPath(configuration["Exports:Directory"] ?? Path.Combine(Path.GetTempPath(), "onvoyage-exports"));

    /// <summary>Writes the part and returns its path relative to the exports area.</summary>
    public async Task<string> WriteAsync(Guid exportId, string service, string json, CancellationToken cancellationToken)
    {
        var relative = $"{exportId:N}/{service}.json";
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, json, new UTF8Encoding(false), cancellationToken);
        return relative;
    }

    public Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken) => File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);

    /// <summary>Removes every part of one export (after assembly or expiry). Missing files are fine.</summary>
    public void Delete(Guid exportId)
    {
        var directory = Resolve($"{exportId:N}");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the exports area.", nameof(relative));
        }

        return full;
    }
}

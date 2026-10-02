using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnVoyage.Creators.Application.Ports;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>
/// Copies the thumbnail of an imported content into the media folder (F-27, licence of the creator terms) and deletes it with the content or the
/// connection. The address comes from a third party, so it is only fetched if it is https, on a known image host, answers 200 without a redirect,
/// is an image and is small. Not resized to WebP 480 px: no image library is part of the allowed dependencies (§10), the platforms already serve
/// thumbnails of that order.
/// </summary>
internal sealed partial class ThumbnailStore(HttpClient http, IOptions<SocialOptions> options, ILogger<ThumbnailStore> logger) : IThumbnailStore
{
    [GeneratedRegex(@"^creators/[0-9a-f]{32}/[0-9a-f]{32}\.(jpg|png|webp)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Ours();

    public async Task<string?> SaveAsync(Guid creatorId, Guid contentId, string sourceUrl, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.MediaDirectory) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !HostAllowed(uri.Host, settings.ThumbnailHosts))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/jpeg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                _ => null,
            };
            if (!response.IsSuccessStatusCode || extension is null || response.Content.Headers.ContentLength > settings.ThumbnailMaxBytes)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > settings.ThumbnailMaxBytes)
                {
                    return null;
                }
            }

            var relative = $"creators/{creatorId:N}/{contentId:N}.{extension}";
            var path = Path.Combine(settings.MediaDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken);
            return relative;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("A thumbnail could not be copied: {Error}.", exception.GetType().Name);
            return null;
        }
    }

    public Task DeleteAsync(string coverPath, CancellationToken cancellationToken)
    {
        // Only the files this store wrote, by their exact shape: a path typed by a person never reaches the file system.
        if (string.IsNullOrWhiteSpace(options.Value.MediaDirectory) || !Ours().IsMatch(coverPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(Path.Combine(options.Value.MediaDirectory, coverPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (IOException exception)
        {
            logger.LogWarning("A thumbnail could not be deleted: {Error}.", exception.GetType().Name);
        }

        return Task.CompletedTask;
    }

    internal static bool HostAllowed(string host, IEnumerable<string> allowed) =>
        allowed.Any(pattern => pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase));
}

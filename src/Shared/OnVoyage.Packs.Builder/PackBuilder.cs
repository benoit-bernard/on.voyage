using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace OnVoyage.Packs.Builder;

/// <summary>
/// Builds the pack of §14.6 (T-307): <c>pack.db</c> (SQLite with R*Tree and FTS5), the audio files, the map extract and the manifest with a
/// SHA-256 per file, then one archive. The build is deterministic: places, stories and parts go in sorted order, the zip entries are sorted and
/// carry a fixed date, so the same content gives the same bytes (only <c>createdAt</c> in the manifest changes, and the caller decides it).
/// </summary>
public static class PackBuilder
{
    private static readonly DateTimeOffset FixedEntryDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly object InitLock = new();
    private static bool _initialised;

    /// <summary>Writes the pack directory (<paramref name="directory"/> must be empty or absent) and its manifest.</summary>
    public static async Task<PackManifest> BuildDirectoryAsync(PackContent content, string directory, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        Initialise();
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new InvalidOperationException("The pack directory must be empty.");
        }

        var places = content.Places.OrderBy(p => p.Slug, StringComparer.Ordinal).ThenBy(p => p.Id).ToArray();
        var audioNames = await CopyAudioAsync(places, directory, cancellationToken);
        if (content.MapPath is { } map && File.Exists(map))
        {
            File.Copy(map, Path.Combine(directory, "map.pmtiles"));
        }

        var dbPath = Path.Combine(directory, "pack.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            Execute(connection, transaction, PackSchema.CreateSql);
            Meta(connection, transaction, "schema_version", PackSchema.Version.ToString(CultureInfo.InvariantCulture));
            Meta(connection, transaction, "destination", content.Destination);
            Meta(connection, transaction, "lang", content.Lang);
            Meta(connection, transaction, "version", content.Version.ToString(CultureInfo.InvariantCulture));
            if (content.TaxonomyVersion is { } taxonomy)
            {
                Meta(connection, transaction, "taxonomy_version", taxonomy);
            }

            var rowid = 0;
            foreach (var place in places)
            {
                InsertPlace(connection, transaction, ++rowid, place);
                foreach (var story in place.Stories.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Version).ThenBy(s => s.Id))
                {
                    InsertStory(connection, transaction, place, story, audioNames);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            Execute(connection, null, "PRAGMA journal_mode = DELETE; VACUUM;");
        }

        SqliteConnection.ClearAllPools();
        return await PackManifests.WriteAsync(directory, content.Destination, content.Lang, content.Version, content.TaxonomyVersion, content.MinAppVersion, createdAt, cancellationToken);
    }

    /// <summary>Zips a built pack directory into one archive (entries sorted, fixed date) and describes it.</summary>
    public static async Task<PackInfo> ArchiveAsync(string directory, string archivePath, PackManifest manifest, string? archiveRelativePath = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(directory, path).Replace('\\', '/');
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                entry.LastWriteTime = FixedEntryDate;
                await using var entryStream = await entry.OpenAsync(cancellationToken);
                await using var source = File.OpenRead(path);
                await source.CopyToAsync(entryStream, cancellationToken);
            }
        }

        return new PackInfo(
            manifest.Destination,
            manifest.Lang,
            manifest.Version,
            archiveRelativePath ?? Path.GetFileName(archivePath),
            new FileInfo(archivePath).Length,
            await PackManifests.HashFileAsync(archivePath, cancellationToken),
            manifest.TotalSize,
            manifest.MinAppVersion,
            manifest.CreatedAt ?? DateTimeOffset.UnixEpoch);
    }

    private static void Initialise()
    {
        lock (InitLock)
        {
            if (!_initialised)
            {
                SQLitePCL.Batteries_V2.Init();
                _initialised = true;
            }
        }
    }

    private static string AudioName(PackStory story, PackAudio audio) => $"audio/{story.Id:N}_v{story.Version}_{audio.Part}.mp3";

    private static async Task<Dictionary<(Guid Story, string Part), string>> CopyAudioAsync(PackPlace[] places, string directory, CancellationToken cancellationToken)
    {
        Dictionary<(Guid, string), string> names = [];
        foreach (var story in places.SelectMany(p => p.Stories))
        {
            foreach (var audio in story.Audio)
            {
                if (!File.Exists(audio.SourcePath))
                {
                    continue; // a part that is not on disk is left out; the app falls back to the text
                }

                var name = AudioName(story, audio);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, name))!);
                await using var source = File.OpenRead(audio.SourcePath);
                await using var target = File.Create(Path.Combine(directory, name));
                await source.CopyToAsync(target, cancellationToken);
                names[(story.Id, audio.Part)] = name;
            }
        }

        return names;
    }

    private static void Meta(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
        Execute(connection, transaction, "INSERT INTO meta (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));

    private static void InsertPlace(SqliteConnection connection, SqliteTransaction transaction, int rowid, PackPlace place)
    {
        var main = place.Stories.OrderBy(s => s.Kind == "standard" ? 0 : 1).ThenBy(s => s.Id).FirstOrDefault();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO poi (rowid_, id, slug, name, category, lat, lng, importance, quality, crowd, hidden_gem, fragile, access_regulated, car_accessible, visible_from_road, story_id)
            VALUES ($r, $id, $slug, $name, $cat, $lat, $lng, $imp, $q, $crowd, $gem, $fragile, $regulated, $car, $road, $story);
            INSERT INTO poi_text (poi_id, name, summary) VALUES ($id, $name, $summary);
            INSERT INTO poi_rtree VALUES ($r, $lat, $lat, $lng, $lng);
            """,
            ("$r", rowid), ("$id", place.Id.ToString()), ("$slug", place.Slug), ("$name", place.Name), ("$cat", place.Category),
            ("$lat", place.Latitude), ("$lng", place.Longitude), ("$imp", place.Importance), ("$q", place.Quality), ("$crowd", place.CrowdLevel),
            ("$gem", place.HiddenGem ? 1 : 0), ("$fragile", place.Fragile ? 1 : 0), ("$regulated", place.AccessRegulated ? 1 : 0),
            ("$car", place.CarAccessible ? 1 : 0), ("$road", place.VisibleFromRoad ? 1 : 0), ("$story", main?.Id.ToString()), ("$summary", place.Summary ?? string.Empty));

        foreach (var (code, weight) in place.Interests.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            Execute(connection, transaction, "INSERT INTO poi_interest (poi_id, code, weight) VALUES ($p, $c, $w)", ("$p", place.Id.ToString()), ("$c", code), ("$w", weight));
        }
    }

    private static void InsertStory(SqliteConnection connection, SqliteTransaction transaction, PackPlace place, PackStory story, Dictionary<(Guid Story, string Part), string> audioNames)
    {
        Execute(
            connection,
            transaction,
            "INSERT INTO story (id, poi_id, kind, title, text, remote_intro, duration_s, ai_generated) VALUES ($id, $poi, $kind, $title, $text, $intro, $d, $ai)",
            ("$id", story.Id.ToString()), ("$poi", place.Id.ToString()), ("$kind", story.Kind), ("$title", story.Title), ("$text", story.Text),
            ("$intro", story.RemoteIntro), ("$d", story.DurationSeconds), ("$ai", story.AiGenerated ? 1 : 0));

        foreach (var audio in story.Audio.OrderBy(a => a.Part, StringComparer.Ordinal))
        {
            if (audioNames.TryGetValue((story.Id, audio.Part), out var name))
            {
                Execute(connection, transaction, "INSERT INTO story_audio_part (story_id, part, path, duration_s) VALUES ($s, $p, $path, $d)", ("$s", story.Id.ToString()), ("$p", audio.Part), ("$path", name), ("$d", audio.DurationSeconds));
            }
        }

        foreach (var source in story.Sources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Execute(connection, transaction, "INSERT INTO story_source (story_id, source) VALUES ($s, $src)", ("$s", story.Id.ToString()), ("$src", source));
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}

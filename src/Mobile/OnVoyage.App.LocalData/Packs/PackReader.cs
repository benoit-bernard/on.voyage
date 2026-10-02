using Microsoft.Data.Sqlite;
using OnVoyage.App.Core.Discovery;
using OnVoyage.Packs;

namespace OnVoyage.App.LocalData.Packs;

public sealed record PackPoi(Guid Id, string Name, string Category, double Lat, double Lng, int Importance, int Crowd, bool Fragile, bool CarAccessible, bool VisibleFromRoad, Guid? StoryId);

public sealed record PackAudioPart(string Part, string Path, int DurationSeconds);

/// <summary>
/// Read-only access to one downloaded pack (§14.6): the manifest is checked against every file's size and SHA-256 before the database is
/// opened, so a truncated or tampered download never reaches the engine. Nearby places come from the R*Tree, text search from FTS5.
/// </summary>
public sealed class PackReader : IDisposable
{
    private readonly SqliteConnection _connection;

    private PackReader(string directory, PackManifest manifest, SqliteConnection connection)
    {
        Directory = directory;
        Manifest = manifest;
        _connection = connection;
    }

    public string Directory { get; }

    public PackManifest Manifest { get; }

    /// <summary>Verifies, then opens the pack in <paramref name="directory"/>. Throws <see cref="PackIntegrityException"/> on any mismatch.</summary>
    public static async Task<PackReader> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        var manifest = await PackManifests.ReadAsync(directory, cancellationToken);
        await PackManifests.VerifyAsync(directory, manifest, cancellationToken);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "pack.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return new PackReader(directory, manifest, connection);
    }

    /// <summary>Places inside <paramref name="radiusMeters"/> of a point, nearest first. The R*Tree narrows to a box, then the exact distance decides.</summary>
    public async Task<IReadOnlyList<PackPoi>> NearbyAsync(double lat, double lng, double radiusMeters, CancellationToken cancellationToken = default)
    {
        var dLat = radiusMeters / 111_320d;
        var dLng = radiusMeters / (111_320d * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, p.category, p.lat, p.lng, p.importance, p.crowd, p.fragile, p.car_accessible, p.visible_from_road, p.story_id
            FROM poi_rtree r JOIN poi p ON p.rowid_ = r.id
            WHERE r.min_lat <= $maxLat AND r.max_lat >= $minLat AND r.min_lng <= $maxLng AND r.max_lng >= $minLng
            """;
        command.Parameters.AddWithValue("$minLat", lat - dLat);
        command.Parameters.AddWithValue("$maxLat", lat + dLat);
        command.Parameters.AddWithValue("$minLng", lng - dLng);
        command.Parameters.AddWithValue("$maxLng", lng + dLng);

        var found = new List<(PackPoi Poi, double Distance)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var poi = Read(reader);
            var distance = GeoMath.DistanceMeters(lat, lng, poi.Lat, poi.Lng);
            if (distance <= radiusMeters)
            {
                found.Add((poi, distance));
            }
        }

        return [.. found.OrderBy(f => f.Distance).Select(f => f.Poi)];
    }

    /// <summary>Offline search (F-14): every word must match the start of a word of the name or summary; accents and case are ignored.</summary>
    public async Task<IReadOnlyList<PackPoi>> SearchAsync(string text, int limit = 20, CancellationToken cancellationToken = default)
    {
        var words = text.Split([' ', '\t', '-', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]))
            .Where(w => w.Length > 0)
            .ToArray();
        if (words.Length == 0)
        {
            return [];
        }

        // Each word is quoted, so user input can never be read as FTS5 syntax.
        var match = string.Join(' ', words.Select(w => $"\"{w}\"*"));
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, p.category, p.lat, p.lng, p.importance, p.crowd, p.fragile, p.car_accessible, p.visible_from_road, p.story_id
            FROM poi_text t JOIN poi p ON p.id = t.poi_id
            WHERE poi_text MATCH $match
            ORDER BY rank, p.importance DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<PackPoi>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<PackAudioPart>> AudioPartsAsync(Guid storyId, CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT part, path, duration_s FROM story_audio_part WHERE story_id = $id ORDER BY part";
        command.Parameters.AddWithValue("$id", storyId.ToString());
        var result = new List<PackAudioPart>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PackAudioPart(reader.GetString(0), Path.Combine(Directory, reader.GetString(1)), reader.GetInt32(2)));
        }

        return result;
    }

    public void Dispose() => _connection.Dispose();

    private static PackPoi Read(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.GetString(1),
        r.GetString(2),
        r.GetDouble(3),
        r.GetDouble(4),
        r.GetInt32(5),
        r.GetInt32(6),
        r.GetInt32(7) != 0,
        r.GetInt32(8) != 0,
        r.GetInt32(9) != 0,
        r.IsDBNull(10) ? null : Guid.Parse(r.GetString(10)));
}

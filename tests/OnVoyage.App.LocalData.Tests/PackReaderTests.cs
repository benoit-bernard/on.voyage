using Microsoft.Data.Sqlite;
using OnVoyage.App.LocalData.Packs;

namespace OnVoyage.App.LocalData.Tests;

public sealed class PackReaderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pack-{Guid.NewGuid():N}");
    private static readonly Guid Panier = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Fort = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Loin = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid StoryPanier = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private async Task BuildAsync()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "audio"));
        await File.WriteAllBytesAsync(Path.Combine(_directory, "audio", "panier_main.mp3"), [1, 2, 3, 4], Ct);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "pack.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            Run(connection, PackSchema.CreateSql);
            Insert(connection, 1, Panier, "Le Panier", "quartier", 43.2985, 5.3690, 80, "Quartier historique de Marseille, rues étroites", StoryPanier);
            Insert(connection, 2, Fort, "Fort Saint-Jean", "monument", 43.2956, 5.3606, 75, "Forteresse à l'entrée du Vieux-Port", null);
            Insert(connection, 3, Loin, "Château d'If", "monument", 43.2800, 5.3250, 90, "Île et prison célèbre", null);
            Run(connection, $"INSERT INTO story_audio_part VALUES ('{StoryPanier}', 'main', 'audio/panier_main.mp3', 90), ('{StoryPanier}', 'announce_front', 'audio/panier_main.mp3', 6)");
        }

        SqliteConnection.ClearAllPools();
        await PackReader.WriteManifestAsync(_directory, "marseille", "fr", 1, Ct);
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection c, int rowid, Guid id, string name, string category, double lat, double lng, int importance, string summary, Guid? story)
    {
        using var command = c.CreateCommand();
        command.CommandText = """
            INSERT INTO poi (rowid_, id, name, category, lat, lng, importance, story_id) VALUES ($r, $id, $name, $cat, $lat, $lng, $imp, $story);
            INSERT INTO poi_text (poi_id, name, summary) VALUES ($id, $name, $summary);
            INSERT INTO poi_rtree VALUES ($r, $lat, $lat, $lng, $lng);
            """;
        command.Parameters.AddWithValue("$r", rowid);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$cat", category);
        command.Parameters.AddWithValue("$lat", lat);
        command.Parameters.AddWithValue("$lng", lng);
        command.Parameters.AddWithValue("$imp", importance);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$story", story?.ToString() ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Opens_a_pack_whose_files_match_the_manifest()
    {
        await BuildAsync();
        using var pack = await PackReader.OpenAsync(_directory, Ct);
        pack.Manifest.Destination.ShouldBe("marseille");
        pack.Manifest.Files.Select(f => f.Path).ShouldBe(["audio/panier_main.mp3", "pack.db"]);
    }

    [Fact]
    public async Task A_changed_file_is_refused()
    {
        await BuildAsync();
        await File.WriteAllBytesAsync(Path.Combine(_directory, "audio", "panier_main.mp3"), [9, 9, 9, 9], Ct);
        var error = await Should.ThrowAsync<PackIntegrityException>(() => PackReader.OpenAsync(_directory, Ct));
        error.Message.ShouldContain("SHA-256");
    }

    [Fact]
    public async Task A_truncated_file_is_refused()
    {
        await BuildAsync();
        await File.WriteAllBytesAsync(Path.Combine(_directory, "audio", "panier_main.mp3"), [1], Ct);
        await Should.ThrowAsync<PackIntegrityException>(() => PackReader.OpenAsync(_directory, Ct));
    }

    [Fact]
    public async Task A_missing_manifest_is_refused()
    {
        Directory.CreateDirectory(_directory);
        await Should.ThrowAsync<PackIntegrityException>(() => PackReader.OpenAsync(_directory, Ct));
    }

    [Fact]
    public async Task Nearby_uses_the_rtree_then_the_exact_distance_and_sorts_by_distance()
    {
        await BuildAsync();
        using var pack = await PackReader.OpenAsync(_directory, Ct);

        var near = await pack.NearbyAsync(43.2970, 5.3650, 600, Ct);
        near.Select(p => p.Name).ShouldBe(["Le Panier", "Fort Saint-Jean"]);

        // The bounding box of 300 m around the Fort corner would hold Panier's box corner but not its exact distance.
        (await pack.NearbyAsync(43.2970, 5.3650, 100, Ct)).ShouldBeEmpty();
        (await pack.NearbyAsync(43.2800, 5.3250, 100, Ct)).Single().Name.ShouldBe("Château d'If");
    }

    [Fact]
    public async Task Search_ignores_accents_and_case_and_matches_prefixes()
    {
        await BuildAsync();
        using var pack = await PackReader.OpenAsync(_directory, Ct);

        (await pack.SearchAsync("chateau", cancellationToken: Ct)).Single().Name.ShouldBe("Château d'If");
        (await pack.SearchAsync("FORT sain", cancellationToken: Ct)).Single().Name.ShouldBe("Fort Saint-Jean");
        (await pack.SearchAsync("vieux-port", cancellationToken: Ct)).Single().Name.ShouldBe("Fort Saint-Jean");
        (await pack.SearchAsync("zzz", cancellationToken: Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_input_is_never_read_as_query_syntax()
    {
        await BuildAsync();
        using var pack = await PackReader.OpenAsync(_directory, Ct);
        (await pack.SearchAsync("\"fort\" OR name:*", cancellationToken: Ct)).ShouldNotBeNull();
        (await pack.SearchAsync("   ", cancellationToken: Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Audio_parts_resolve_to_files_inside_the_pack()
    {
        await BuildAsync();
        using var pack = await PackReader.OpenAsync(_directory, Ct);
        var parts = await pack.AudioPartsAsync(StoryPanier, Ct);
        parts.Select(p => p.Part).ShouldBe(["announce_front", "main"]);
        File.Exists(parts[1].Path).ShouldBeTrue();
        (await pack.NearbyAsync(43.2985, 5.3690, 50, Ct)).Single().StoryId.ShouldBe(StoryPanier);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}

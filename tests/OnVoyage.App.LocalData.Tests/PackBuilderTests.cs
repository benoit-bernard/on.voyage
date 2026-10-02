using System.IO.Compression;
using Microsoft.Data.Sqlite;
using OnVoyage.App.LocalData.Packs;
using OnVoyage.Packs;
using OnVoyage.Packs.Builder;

namespace OnVoyage.App.LocalData.Tests;

/// <summary>The pack builder of Factory (T-307) against the reader of the app (T-617): what one writes, the other opens.</summary>
public sealed class PackBuilderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"packbuilder-{Guid.NewGuid():N}");
    private readonly string _audio;

    private static readonly Guid Major = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Fort = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Calanque = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid MajorStory = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FortStory = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    public PackBuilderTests()
    {
        _audio = Path.Combine(_root, "media");
        Directory.CreateDirectory(_audio);
        File.WriteAllBytes(Path.Combine(_audio, "major_main.mp3"), [1, 2, 3, 4, 5]);
        File.WriteAllBytes(Path.Combine(_audio, "major_front.mp3"), [9, 9]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PackContent Content(bool reversed = false, int version = 1)
    {
        PackPlace[] places =
        [
            Place(Major, "cathedrale-de-la-major", "Cathédrale de la Major", "religion", 43.3003, 5.3645, 70, "Une immense cathédrale romano-byzantine.", [Story(MajorStory, "La Major", "major")]),
            Place(Fort, "fort-saint-jean", "Fort Saint-Jean", "architecture", 43.2960, 5.3618, 80, "Forteresse à l'entrée du Vieux-Port.", [Story(FortStory, "Le Fort", "fort")]),
            Place(Calanque, "calanque-de-sormiou", "Calanque de Sormiou", "nature", 43.2145, 5.4225, 82, "Une calanque sauvage.", []),
        ];
        return new PackContent("marseille", "fr", version, "1", "1.0.0", reversed ? [.. places.Reverse()] : places);
    }

    private static PackPlace Place(Guid id, string slug, string name, string category, double lat, double lng, int importance, string summary, IReadOnlyList<PackStory> stories) =>
        new(id, slug, name, category, lat, lng, importance, 2, false, false, importance < 75, 0.8, false, false, summary, new Dictionary<string, double> { [category] = 0.9, [$"{category}.sub"] = 1.0 }, stories);

    private PackStory Story(Guid id, string title, string audioName) =>
        new(id, 1, "standard", title, $"Texte de {title}.", $"Avant d'y aller, {title}.", 90, true, ["Wikipédia — CC BY-SA"],
            [new PackAudio("main", Path.Combine(_audio, $"{audioName}_main.mp3"), 90), new PackAudio("announce_front", Path.Combine(_audio, $"{audioName}_front.mp3"), 5)]);

    [Fact]
    public async Task A_built_pack_opens_in_the_app_reader_with_places_search_audio_and_manifest()
    {
        var directory = Path.Combine(_root, "pack");
        var manifest = await PackBuilder.BuildDirectoryAsync(Content(), directory, Created, Ct);

        using var pack = await PackReader.OpenAsync(directory, Ct);

        pack.Manifest.Destination.ShouldBe("marseille");
        pack.Manifest.CreatedAt.ShouldBe(Created);
        manifest.Files.Select(file => file.Path).ShouldBe([$"audio/{MajorStory:N}_v1_announce_front.mp3", $"audio/{MajorStory:N}_v1_main.mp3", "pack.db"], "sorted; the audio of the fort is not on disk and is left out");

        (await pack.NearbyAsync(43.2965, 5.3620, 1_500, Ct)).Select(p => p.Name).ShouldBe(["Fort Saint-Jean", "Cathédrale de la Major"]);
        (await pack.SearchAsync("cathedrale", 5, Ct)).Select(p => p.Name).ShouldBe(["Cathédrale de la Major"]);
        (await pack.SearchAsync("romano", 5, Ct)).Select(p => p.Name).ShouldBe(["Cathédrale de la Major"], "the summary is searched too");

        var parts = await pack.AudioPartsAsync(MajorStory, Ct);
        parts.Select(part => part.Part).ShouldBe(["announce_front", "main"]);
        parts.ShouldAllBe(part => File.Exists(part.Path));
    }

    [Fact]
    public async Task The_pack_database_carries_what_the_offline_engine_needs()
    {
        var directory = Path.Combine(_root, "pack");
        await PackBuilder.BuildDirectoryAsync(Content(), directory, Created, Ct);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "pack.db")};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(Ct);

        Scalar(connection, "SELECT value FROM meta WHERE key = 'destination'").ShouldBe("marseille");
        Scalar(connection, "SELECT value FROM meta WHERE key = 'schema_version'").ShouldBe(PackSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Scalar(connection, $"SELECT group_concat(code || '=' || weight, ',') FROM (SELECT code, weight FROM poi_interest WHERE poi_id = '{Major}' ORDER BY code)").ShouldBe("religion=0.9,religion.sub=1.0");
        Scalar(connection, $"SELECT title || '|' || text || '|' || remote_intro FROM story WHERE id = '{MajorStory}'").ShouldBe("La Major|Texte de La Major.|Avant d'y aller, La Major.");
        Scalar(connection, $"SELECT source FROM story_source WHERE story_id = '{MajorStory}'").ShouldBe("Wikipédia — CC BY-SA");
        Scalar(connection, "SELECT count(*) FROM poi_rtree").ShouldBe("3");
        Scalar(connection, $"SELECT story_id FROM poi WHERE id = '{Calanque}'").ShouldBe(string.Empty, "a place without story has none");
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task The_same_content_builds_the_same_pack_whatever_the_order_of_the_input()
    {
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "b");
        var firstManifest = await PackBuilder.BuildDirectoryAsync(Content(), first, Created, Ct);
        var secondManifest = await PackBuilder.BuildDirectoryAsync(Content(reversed: true), second, Created, Ct);

        secondManifest.Files.ShouldBe(firstManifest.Files, "same files, sizes and hashes");
        secondManifest.TotalSize.ShouldBe(firstManifest.TotalSize);
        (await PackManifests.HashFileAsync(Path.Combine(first, "pack.db"), Ct)).ShouldBe(await PackManifests.HashFileAsync(Path.Combine(second, "pack.db"), Ct));
        (await File.ReadAllTextAsync(Path.Combine(first, "manifest.json"), Ct)).ShouldBe(await File.ReadAllTextAsync(Path.Combine(second, "manifest.json"), Ct));
    }

    [Fact]
    public async Task Only_the_creation_time_differs_between_two_builds_and_the_archives_are_identical_otherwise()
    {
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "b");
        var firstManifest = await PackBuilder.BuildDirectoryAsync(Content(), first, Created, Ct);
        var secondManifest = await PackBuilder.BuildDirectoryAsync(Content(), second, Created.AddDays(3), Ct);

        secondManifest.Files.ShouldBe(firstManifest.Files);
        secondManifest.CreatedAt.ShouldBe(Created.AddDays(3));
        firstManifest.CreatedAt.ShouldBe(Created);

        var firstArchive = await PackBuilder.ArchiveAsync(first, Path.Combine(_root, "a.zip"), firstManifest, cancellationToken: Ct);
        var again = await PackBuilder.ArchiveAsync(first, Path.Combine(_root, "a2.zip"), firstManifest, cancellationToken: Ct);
        again.ArchiveSha256.ShouldBe(firstArchive.ArchiveSha256, "zip entries are sorted and carry a fixed date");
    }

    [Fact]
    public async Task The_archive_unpacks_to_a_pack_whose_files_match_the_manifest()
    {
        var directory = Path.Combine(_root, "pack");
        var manifest = await PackBuilder.BuildDirectoryAsync(Content(version: 3), directory, Created, Ct);
        var archive = Path.Combine(_root, PackNames.Archive("marseille", "fr", 3));

        var info = await PackBuilder.ArchiveAsync(directory, archive, manifest, "packs/marseille/fr/pack_marseille_fr_v3.zip", Ct);

        info.Version.ShouldBe(3);
        info.ArchiveSize.ShouldBe(new FileInfo(archive).Length);
        info.ArchiveSha256.ShouldBe(await PackManifests.HashFileAsync(archive, Ct));
        info.InstalledSize.ShouldBe(manifest.TotalSize);
        info.ArchivePath.ShouldBe("packs/marseille/fr/pack_marseille_fr_v3.zip");
        var target = Path.Combine(_root, "unpacked");
        await ZipFile.ExtractToDirectoryAsync(archive, target, Ct);
        using var pack = await PackReader.OpenAsync(target, Ct);
        pack.Manifest.Version.ShouldBe(3);
        archive.ShouldEndWith("pack_marseille_fr_v3.zip");
    }

    [Fact]
    public async Task An_audio_part_missing_on_disk_is_left_out_and_the_text_remains()
    {
        File.Delete(Path.Combine(_audio, "major_front.mp3"));
        var directory = Path.Combine(_root, "pack");
        await PackBuilder.BuildDirectoryAsync(Content(), directory, Created, Ct);

        using var pack = await PackReader.OpenAsync(directory, Ct);

        (await pack.AudioPartsAsync(MajorStory, Ct)).Select(part => part.Part).ShouldBe(["main"]);
    }

    [Fact]
    public async Task A_directory_that_is_not_empty_is_refused()
    {
        var directory = Path.Combine(_root, "pack");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "stale.txt"), "x", Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => PackBuilder.BuildDirectoryAsync(Content(), directory, Created, Ct));
    }
}

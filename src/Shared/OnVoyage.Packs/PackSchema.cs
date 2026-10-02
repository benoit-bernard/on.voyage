namespace OnVoyage.Packs;

/// <summary>
/// The <c>pack.db</c> schema of §14.6, shared by the builder (Factory, T-307) and the reader (the app, T-617) so that they cannot drift apart.
/// Places carry their interest weights (the on-device recommendation engine needs them offline, T-620), their stories with text and audio parts,
/// and an R*Tree for the spatial queries of the discovery mode. Search is FTS5 without accents. Images and the taxonomy table of §14.6 are not
/// included yet: there is no image pipeline, and the taxonomy ships with the app (its version is in <c>meta</c>).
/// </summary>
public static class PackSchema
{
    /// <summary>Bumped when a table or a column changes in a way an older reader cannot ignore.</summary>
    public const int Version = 1;

    public const string CreateSql = """
        CREATE TABLE meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE poi (
            rowid_ INTEGER PRIMARY KEY,
            id TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            category TEXT NOT NULL,
            lat REAL NOT NULL,
            lng REAL NOT NULL,
            importance INTEGER NOT NULL,
            crowd INTEGER NOT NULL DEFAULT 0,
            fragile INTEGER NOT NULL DEFAULT 0,
            car_accessible INTEGER NOT NULL DEFAULT 0,
            visible_from_road INTEGER NOT NULL DEFAULT 0,
            story_id TEXT,
            slug TEXT NOT NULL DEFAULT '',
            quality REAL NOT NULL DEFAULT 0,
            hidden_gem INTEGER NOT NULL DEFAULT 0,
            access_regulated INTEGER NOT NULL DEFAULT 0
        );
        CREATE VIRTUAL TABLE poi_text USING fts5(poi_id UNINDEXED, name, summary, tokenize = 'unicode61 remove_diacritics 2');
        CREATE VIRTUAL TABLE poi_rtree USING rtree(id, min_lat, max_lat, min_lng, max_lng);
        CREATE TABLE poi_interest (
            poi_id TEXT NOT NULL,
            code TEXT NOT NULL,
            weight REAL NOT NULL,
            PRIMARY KEY (poi_id, code)
        );
        CREATE TABLE story (
            id TEXT PRIMARY KEY,
            poi_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            title TEXT NOT NULL,
            text TEXT NOT NULL,
            remote_intro TEXT NOT NULL DEFAULT '',
            duration_s INTEGER NOT NULL DEFAULT 0,
            ai_generated INTEGER NOT NULL DEFAULT 1
        );
        CREATE TABLE story_audio_part (
            story_id TEXT NOT NULL,
            part TEXT NOT NULL,
            path TEXT NOT NULL,
            duration_s INTEGER NOT NULL,
            PRIMARY KEY (story_id, part)
        );
        CREATE TABLE story_source (
            story_id TEXT NOT NULL,
            source TEXT NOT NULL,
            PRIMARY KEY (story_id, source)
        );
        """;
}

namespace OnVoyage.App.LocalData.Packs;

/// <summary>The subset of the §14.6 <c>pack.db</c> schema the app reads. The pack builder (T-307) creates it with this same script.</summary>
public static class PackSchema
{
    public const string CreateSql = """
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
            story_id TEXT
        );
        CREATE VIRTUAL TABLE poi_text USING fts5(poi_id UNINDEXED, name, summary, tokenize = 'unicode61 remove_diacritics 2');
        CREATE VIRTUAL TABLE poi_rtree USING rtree(id, min_lat, max_lat, min_lng, max_lng);
        CREATE TABLE story_audio_part (
            story_id TEXT NOT NULL,
            part TEXT NOT NULL,
            path TEXT NOT NULL,
            duration_s INTEGER NOT NULL,
            PRIMARY KEY (story_id, part)
        );
        """;
}

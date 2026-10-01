using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFactory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "factory");

            migrationBuilder.EnsureSchema(
                name: "factory_raw");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "dedup_link",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kept_place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    other_place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    similarity = table.Column<double>(type: "double precision", nullable: false),
                    distance_meters = table.Column<double>(type: "double precision", nullable: false),
                    automatic = table.Column<bool>(type: "boolean", nullable: false),
                    inherited_qid = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reverted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dedup_link", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_run",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_slug = table.Column<string>(type: "text", nullable: false),
                    raw_table = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    raw_rows = table.Column<int>(type: "integer", nullable: false),
                    created = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "place",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_slug = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                    footprint = table.Column<MultiPolygon>(type: "geometry (multipolygon, 4326)", nullable: true),
                    qid = table.Column<string>(type: "text", nullable: true),
                    osm_type = table.Column<string>(type: "text", nullable: false),
                    osm_id = table.Column<long>(type: "bigint", nullable: false),
                    osm_version = table.Column<int>(type: "integer", nullable: true),
                    osm_tags = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    annual_pageviews = table.Column<long>(type: "bigint", nullable: false),
                    importance_score = table.Column<short>(type: "smallint", nullable: true),
                    popularity_percentile = table.Column<short>(type: "smallint", nullable: true),
                    hidden_gem = table.Column<bool>(type: "boolean", nullable: false),
                    crowd_offpeak = table.Column<short>(type: "smallint", nullable: false),
                    crowd_shoulder = table.Column<short>(type: "smallint", nullable: false),
                    crowd_peak = table.Column<short>(type: "smallint", nullable: false),
                    classification_outcome = table.Column<string>(type: "text", nullable: true),
                    classification_confidence = table.Column<float>(type: "real", nullable: false),
                    importance_override = table.Column<short>(type: "smallint", nullable: true),
                    editorially_saturated = table.Column<bool>(type: "boolean", nullable: false),
                    fragile = table.Column<bool>(type: "boolean", nullable: false),
                    access_regulated = table.Column<bool>(type: "boolean", nullable: false),
                    merged_into = table.Column<Guid>(type: "uuid", nullable: true),
                    published_version = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_license = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_place", x => x.id);
                    table.CheckConstraint("ck_place_crowd", "crowd_offpeak between 1 and 5 and crowd_shoulder between 1 and 5 and crowd_peak between 1 and 5");
                    table.CheckConstraint("ck_place_importance", "importance_score is null or importance_score between 0 and 100");
                });

            migrationBuilder.CreateTable(
                name: "wikidata_entity",
                schema: "factory_raw",
                columns: table => new
                {
                    qid = table.Column<string>(type: "text", nullable: false),
                    label_fr = table.Column<string>(type: "text", nullable: true),
                    label_en = table.Column<string>(type: "text", nullable: true),
                    description_fr = table.Column<string>(type: "text", nullable: true),
                    description_en = table.Column<string>(type: "text", nullable: true),
                    instance_of = table.Column<string[]>(type: "text[]", nullable: false),
                    heritage_statuses = table.Column<string[]>(type: "text[]", nullable: false),
                    inception = table.Column<string>(type: "text", nullable: true),
                    sitelinks = table.Column<int>(type: "integer", nullable: false),
                    wikipedia_fr = table.Column<string>(type: "text", nullable: true),
                    wikipedia_en = table.Column<string>(type: "text", nullable: true),
                    image = table.Column<string>(type: "text", nullable: true),
                    website = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_license = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikidata_entity", x => x.qid);
                });

            migrationBuilder.CreateTable(
                name: "wikipedia_pageviews",
                schema: "factory_raw",
                columns: table => new
                {
                    qid = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    views12_months = table.Column<long>(type: "bigint", nullable: false),
                    source_license = table.Column<string>(type: "text", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikipedia_pageviews", x => new { x.qid, x.language });
                });

            migrationBuilder.CreateTable(
                name: "place_interest",
                schema: "factory",
                columns: table => new
                {
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taxonomy_code = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<float>(type: "real", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_place_interest", x => new { x.place_id, x.taxonomy_code });
                    table.CheckConstraint("ck_place_interest_weight", "weight > 0 and weight <= 1");
                    table.ForeignKey(
                        name: "FK_place_interest_place_place_id",
                        column: x => x.place_id,
                        principalSchema: "factory",
                        principalTable: "place",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dedup_link_kept_place_id",
                schema: "factory",
                table: "dedup_link",
                column: "kept_place_id");

            migrationBuilder.CreateIndex(
                name: "IX_dedup_link_other_place_id",
                schema: "factory",
                table: "dedup_link",
                column: "other_place_id");

            migrationBuilder.CreateIndex(
                name: "IX_place_destination_slug_slug",
                schema: "factory",
                table: "place",
                columns: new[] { "destination_slug", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_place_destination_slug_status",
                schema: "factory",
                table: "place",
                columns: new[] { "destination_slug", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_place_location",
                schema: "factory",
                table: "place",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_place_name",
                schema: "factory",
                table: "place",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_place_osm_type_osm_id",
                schema: "factory",
                table: "place",
                columns: new[] { "osm_type", "osm_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_place_qid",
                schema: "factory",
                table: "place",
                column: "qid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dedup_link",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "import_run",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "place_interest",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "wikidata_entity",
                schema: "factory_raw");

            migrationBuilder.DropTable(
                name: "wikipedia_pageviews",
                schema: "factory_raw");

            migrationBuilder.DropTable(
                name: "place",
                schema: "factory");
        }
    }
}

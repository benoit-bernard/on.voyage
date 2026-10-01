using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using NpgsqlTypes;

#nullable disable

namespace OnVoyage.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "destination",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name_fr = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: true),
                    center = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "taxonomy_node",
                schema: "catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    parent_code = table.Column<string>(type: "text", nullable: true),
                    level = table.Column<short>(type: "smallint", nullable: false),
                    taxonomy_version = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_taxonomy_node", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "poi",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                    importance_score = table.Column<short>(type: "smallint", nullable: false),
                    hidden_gem = table.Column<bool>(type: "boolean", nullable: false),
                    content_quality_score = table.Column<float>(type: "real", nullable: false),
                    taxonomy_version = table.Column<int>(type: "integer", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi", x => x.id);
                    table.CheckConstraint("ck_poi_importance", "importance_score between 0 and 100");
                    table.ForeignKey(
                        name: "FK_poi_destination_destination_id",
                        column: x => x.destination_id,
                        principalSchema: "catalog",
                        principalTable: "destination",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poi_ethics",
                schema: "catalog",
                columns: table => new
                {
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fragile = table.Column<bool>(type: "boolean", nullable: false),
                    access_regulated = table.Column<bool>(type: "boolean", nullable: false),
                    crowd_profile = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_ethics", x => x.poi_id);
                    table.ForeignKey(
                        name: "FK_poi_ethics_poi_poi_id",
                        column: x => x.poi_id,
                        principalSchema: "catalog",
                        principalTable: "poi",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poi_interest",
                schema: "catalog",
                columns: table => new
                {
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taxonomy_code = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_interest", x => new { x.poi_id, x.taxonomy_code });
                    table.CheckConstraint("ck_poi_interest_weight", "weight > 0 and weight <= 1");
                    table.ForeignKey(
                        name: "FK_poi_interest_poi_poi_id",
                        column: x => x.poi_id,
                        principalSchema: "catalog",
                        principalTable: "poi",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_poi_interest_taxonomy_node_taxonomy_code",
                        column: x => x.taxonomy_code,
                        principalSchema: "catalog",
                        principalTable: "taxonomy_node",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poi_text",
                schema: "catalog",
                columns: table => new
                {
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    short_description = table.Column<string>(type: "text", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "french")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "name", "short_description" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_text", x => new { x.poi_id, x.lang });
                    table.ForeignKey(
                        name: "FK_poi_text_poi_poi_id",
                        column: x => x.poi_id,
                        principalSchema: "catalog",
                        principalTable: "poi",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    audio_path = table.Column<string>(type: "text", nullable: true),
                    is_premium = table.Column<bool>(type: "boolean", nullable: false),
                    is_ai_generated = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story", x => x.id);
                    table.CheckConstraint("ck_story_premium_text", "not is_premium or text is null");
                    table.CheckConstraint("ck_story_status", "status in ('published', 'unpublished', 'archived')");
                    table.ForeignKey(
                        name: "FK_story_poi_poi_id",
                        column: x => x.poi_id,
                        principalSchema: "catalog",
                        principalTable: "poi",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_destination_slug",
                schema: "catalog",
                table: "destination",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_poi_destination_id_slug",
                schema: "catalog",
                table: "poi",
                columns: new[] { "destination_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_poi_location",
                schema: "catalog",
                table: "poi",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_poi_interest_taxonomy_code",
                schema: "catalog",
                table: "poi_interest",
                column: "taxonomy_code");

            migrationBuilder.CreateIndex(
                name: "IX_poi_text_name",
                schema: "catalog",
                table: "poi_text",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_poi_text_search_vector",
                schema: "catalog",
                table: "poi_text",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_story_current_published",
                schema: "catalog",
                table: "story",
                columns: new[] { "poi_id", "lang" },
                filter: "status = 'published'");

            migrationBuilder.CreateIndex(
                name: "IX_story_poi_id_lang_kind_version",
                schema: "catalog",
                table: "story",
                columns: new[] { "poi_id", "lang", "kind", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poi_ethics",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "poi_interest",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "poi_text",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "story",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "taxonomy_node",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "poi",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "destination",
                schema: "catalog");
        }
    }
}

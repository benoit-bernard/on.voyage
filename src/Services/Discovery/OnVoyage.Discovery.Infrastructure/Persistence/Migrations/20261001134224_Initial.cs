using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OnVoyage.Discovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "discovery");

            migrationBuilder.CreateTable(
                name: "onboarding_clip",
                schema: "discovery",
                columns: table => new
                {
                    story_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    audio_path = table.Column<string>(type: "text", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_clip", x => x.story_id);
                });

            migrationBuilder.CreateTable(
                name: "poi_projection",
                schema: "discovery",
                columns: table => new
                {
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    weights = table.Column<string>(type: "jsonb", nullable: false),
                    importance = table.Column<double>(type: "double precision", nullable: false),
                    quality = table.Column<double>(type: "double precision", nullable: false),
                    hidden_gem = table.Column<bool>(type: "boolean", nullable: false),
                    fragile = table.Column<bool>(type: "boolean", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_projection", x => x.poi_id);
                });

            migrationBuilder.CreateTable(
                name: "traveler",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    ethical_mode = table.Column<string>(type: "text", nullable: false),
                    is_premium = table.Column<bool>(type: "boolean", nullable: false),
                    profile_depth = table.Column<int>(type: "integer", nullable: false),
                    cohort = table.Column<string>(type: "text", nullable: false),
                    last_active_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traveler", x => x.id);
                    table.CheckConstraint("ck_traveler_cohort", "cohort in ('control', 'personalized')");
                });

            migrationBuilder.CreateTable(
                name: "impression",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    surface = table.Column<string>(type: "text", nullable: false),
                    shown_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_impression", x => x.id);
                    table.ForeignKey(
                        name: "FK_impression_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interaction",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: true),
                    story_id = table.Column<Guid>(type: "uuid", nullable: true),
                    story_version = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    category_code = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interaction", x => x.id);
                    table.ForeignKey(
                        name: "FK_interaction_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interest_vector",
                schema: "discovery",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vector = table.Column<float[]>(type: "real[]", nullable: false),
                    taxonomy_version = table.Column<int>(type: "integer", nullable: false),
                    locks = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interest_vector", x => x.traveler_id);
                    table.ForeignKey(
                        name: "FK_interest_vector_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poi_rating",
                schema: "discovery",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<double>(type: "double precision", nullable: false),
                    excluded = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_rating", x => new { x.traveler_id, x.poi_id });
                    table.ForeignKey(
                        name: "FK_poi_rating_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visit",
                schema: "discovery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visited_on = table.Column<DateOnly>(type: "date", nullable: false),
                    dwell_s = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit", x => x.id);
                    table.ForeignKey(
                        name: "FK_visit_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_impression_shown_at",
                schema: "discovery",
                table: "impression",
                column: "shown_at");

            migrationBuilder.CreateIndex(
                name: "IX_impression_traveler_id_client_event_id",
                schema: "discovery",
                table: "impression",
                columns: new[] { "traveler_id", "client_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interaction_traveler_id_client_event_id",
                schema: "discovery",
                table: "interaction",
                columns: new[] { "traveler_id", "client_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interaction_traveler_id_occurred_at",
                schema: "discovery",
                table: "interaction",
                columns: new[] { "traveler_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_clip_lang",
                schema: "discovery",
                table: "onboarding_clip",
                column: "lang");

            migrationBuilder.CreateIndex(
                name: "IX_visit_traveler_id_client_event_id",
                schema: "discovery",
                table: "visit",
                columns: new[] { "traveler_id", "client_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "impression",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "interaction",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "interest_vector",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "onboarding_clip",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "poi_projection",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "poi_rating",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "visit",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "traveler",
                schema: "discovery");
        }
    }
}

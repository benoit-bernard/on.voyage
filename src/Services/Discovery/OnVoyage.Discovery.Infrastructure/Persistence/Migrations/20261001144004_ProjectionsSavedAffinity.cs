using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Discovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectionsSavedAffinity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "access_regulated",
                schema: "discovery",
                table: "poi_projection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "crowd_level",
                schema: "discovery",
                table: "poi_projection",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                schema: "discovery",
                table: "poi_projection",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                schema: "discovery",
                table: "poi_projection",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "discovery",
                table: "poi_projection",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "category_affinity",
                schema: "discovery",
                columns: table => new
                {
                    code_a = table.Column<string>(type: "text", nullable: false),
                    code_b = table.Column<string>(type: "text", nullable: false),
                    lift = table.Column<double>(type: "double precision", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_category_affinity", x => new { x.code_a, x.code_b });
                });

            migrationBuilder.CreateTable(
                name: "saved_poi",
                schema: "discovery",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_poi", x => new { x.traveler_id, x.poi_id });
                    table.ForeignKey(
                        name: "FK_saved_poi_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_projection",
                schema: "discovery",
                columns: table => new
                {
                    story_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    is_premium = table.Column<bool>(type: "boolean", nullable: false),
                    audio_parts = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_projection", x => x.story_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_story_projection_poi_id",
                schema: "discovery",
                table: "story_projection",
                column: "poi_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_affinity",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "saved_poi",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "story_projection",
                schema: "discovery");

            migrationBuilder.DropColumn(
                name: "access_regulated",
                schema: "discovery",
                table: "poi_projection");

            migrationBuilder.DropColumn(
                name: "crowd_level",
                schema: "discovery",
                table: "poi_projection");

            migrationBuilder.DropColumn(
                name: "latitude",
                schema: "discovery",
                table: "poi_projection");

            migrationBuilder.DropColumn(
                name: "longitude",
                schema: "discovery",
                table: "poi_projection");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "discovery",
                table: "poi_projection");
        }
    }
}

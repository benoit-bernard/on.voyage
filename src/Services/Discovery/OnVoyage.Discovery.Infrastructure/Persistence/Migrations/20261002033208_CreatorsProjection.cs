using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Discovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreatorsProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "weights",
                schema: "discovery",
                table: "interaction",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "creator_follow",
                schema: "discovery",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    following = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_follow", x => new { x.traveler_id, x.creator_id });
                    table.ForeignKey(
                        name: "FK_creator_follow_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_place_link",
                schema: "discovery",
                columns: table => new
                {
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_commercial = table.Column<bool>(type: "boolean", nullable: false),
                    validated = table.Column<bool>(type: "boolean", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_place_link", x => new { x.creator_id, x.poi_id, x.content_id });
                });

            migrationBuilder.CreateTable(
                name: "creator_projection",
                schema: "discovery",
                columns: table => new
                {
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    avatar_path = table.Column<string>(type: "text", nullable: true),
                    specialties = table.Column<string>(type: "jsonb", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    vector = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_projection", x => x.creator_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_creator_place_link_poi_id",
                schema: "discovery",
                table: "creator_place_link",
                column: "poi_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "creator_follow",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "creator_place_link",
                schema: "discovery");

            migrationBuilder.DropTable(
                name: "creator_projection",
                schema: "discovery");

            migrationBuilder.DropColumn(
                name: "weights",
                schema: "discovery",
                table: "interaction");
        }
    }
}

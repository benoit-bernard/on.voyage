using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Creators.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeoAssociation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "geotagged_at",
                schema: "creators",
                table: "content_item",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "unmatched_mention",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: true),
                    excerpt = table.Column<string>(type: "text", nullable: true),
                    suggested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unmatched_mention", x => x.id);
                    table.ForeignKey(
                        name: "FK_unmatched_mention_content_item_content_id",
                        column: x => x.content_id,
                        principalSchema: "creators",
                        principalTable: "content_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_unmatched_mention_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_item_creator_id_geotagged_at",
                schema: "creators",
                table: "content_item",
                columns: new[] { "creator_id", "geotagged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_unmatched_mention_content_id_key",
                schema: "creators",
                table: "unmatched_mention",
                columns: new[] { "content_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unmatched_mention_creator_id",
                schema: "creators",
                table: "unmatched_mention",
                column: "creator_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unmatched_mention",
                schema: "creators");

            migrationBuilder.DropIndex(
                name: "IX_content_item_creator_id_geotagged_at",
                schema: "creators",
                table: "content_item");

            migrationBuilder.DropColumn(
                name: "geotagged_at",
                schema: "creators",
                table: "content_item");
        }
    }
}

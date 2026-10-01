using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_link",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: true),
                    thumbnail_path = table.Column<string>(type: "text", nullable: true),
                    video_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_link", x => x.id);
                    table.CheckConstraint("ck_external_link_kind", "kind in ('wikipedia', 'youtube', 'official')");
                    table.ForeignKey(
                        name: "FK_external_link_poi_poi_id",
                        column: x => x.poi_id,
                        principalSchema: "catalog",
                        principalTable: "poi",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_link_poi_id",
                schema: "catalog",
                table: "external_link",
                column: "poi_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_link",
                schema: "catalog");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoryAudioAndSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_parts",
                schema: "catalog",
                table: "story",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sources",
                schema: "catalog",
                table: "story",
                type: "jsonb",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_parts",
                schema: "catalog",
                table: "story");

            migrationBuilder.DropColumn(
                name: "sources",
                schema: "catalog",
                table: "story");
        }
    }
}

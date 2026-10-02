using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "youtube_search",
                schema: "factory",
                columns: table => new
                {
                    query_key = table.Column<string>(type: "text", nullable: false),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_youtube_search", x => x.query_key);
                });

            migrationBuilder.CreateTable(
                name: "youtube_usage",
                schema: "factory",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    units = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_youtube_usage", x => x.day);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "youtube_search",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "youtube_usage",
                schema: "factory");
        }
    }
}

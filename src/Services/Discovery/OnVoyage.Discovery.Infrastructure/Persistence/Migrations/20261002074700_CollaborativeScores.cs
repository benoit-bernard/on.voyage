using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Discovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollaborativeScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cf_score",
                schema: "discovery",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<float>(type: "real", nullable: false),
                    support = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cf_score", x => new { x.traveler_id, x.poi_id });
                    table.CheckConstraint("ck_cf_score_range", "score between -1 and 1 and support > 0");
                    table.ForeignKey(
                        name: "FK_cf_score_traveler_traveler_id",
                        column: x => x.traveler_id,
                        principalSchema: "discovery",
                        principalTable: "traveler",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cf_score_traveler_id_destination",
                schema: "discovery",
                table: "cf_score",
                columns: new[] { "traveler_id", "destination" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cf_score",
                schema: "discovery");
        }
    }
}

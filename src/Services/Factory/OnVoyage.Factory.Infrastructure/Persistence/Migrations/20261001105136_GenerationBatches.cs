using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GenerationBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "generation_batch",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    criteria = table.Column<string>(type: "jsonb", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_batch", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "generation_job",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_name = table.Column<string>(type: "text", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    step = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    story_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_job", x => x.id);
                    table.CheckConstraint("ck_generation_job_state", "state in ('Pending', 'Running', 'Succeeded', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_generation_batch_created_at",
                schema: "factory",
                table: "generation_batch",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_generation_job_batch_id_state",
                schema: "factory",
                table: "generation_job",
                columns: new[] { "batch_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generation_batch",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "generation_job",
                schema: "factory");
        }
    }
}

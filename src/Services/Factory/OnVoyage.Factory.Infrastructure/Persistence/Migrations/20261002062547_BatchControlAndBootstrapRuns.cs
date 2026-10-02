using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BatchControlAndBootstrapRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_job_state",
                schema: "factory",
                table: "generation_job");

            migrationBuilder.CreateTable(
                name: "bootstrap_run",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_places = table.Column<int>(type: "integer", nullable: false),
                    min_importance = table.Column<int>(type: "integer", nullable: true),
                    lang = table.Column<string>(type: "text", nullable: false),
                    auto_publish = table.Column<bool>(type: "boolean", nullable: false),
                    budget_usd = table.Column<double>(type: "double precision", nullable: false),
                    cost_usd = table.Column<double>(type: "double precision", nullable: false),
                    places_total = table.Column<int>(type: "integer", nullable: false),
                    places_done = table.Column<int>(type: "integer", nullable: false),
                    written = table.Column<int>(type: "integer", nullable: false),
                    to_review = table.Column<int>(type: "integer", nullable: false),
                    published = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bootstrap_run", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_job_state",
                schema: "factory",
                table: "generation_job",
                sql: "state in ('Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_bootstrap_run_requested_at",
                schema: "factory",
                table: "bootstrap_run",
                column: "requested_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bootstrap_run",
                schema: "factory");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_job_state",
                schema: "factory",
                table: "generation_job");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_job_state",
                schema: "factory",
                table: "generation_job",
                sql: "state in ('Pending', 'Running', 'Succeeded', 'Failed')");
        }
    }
}

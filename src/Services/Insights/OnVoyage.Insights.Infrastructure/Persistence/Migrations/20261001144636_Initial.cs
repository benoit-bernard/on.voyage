using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Insights.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "insights");

            migrationBuilder.CreateTable(
                name: "config_snapshot",
                schema: "insights",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_snapshot", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "consent_projection",
                schema: "insights",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analytics = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_projection", x => x.traveler_id);
                });

            migrationBuilder.CreateTable(
                name: "daily_kpi",
                schema: "insights",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    destination_id = table.Column<string>(type: "text", nullable: false),
                    cohort = table.Column<string>(type: "text", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_kpi", x => new { x.date, x.destination_id, x.cohort, x.metric });
                });

            // Written by hand: EF Core cannot declare a partitioned table. Monthly partitions of occurred_at (§11.4); the primary key must
            // contain the partition key, and the client's event id plus the moment is what makes a resend a no-op. The indexes below are
            // created on the parent and propagate to every partition.
            migrationBuilder.Sql("""
                create table insights.event (
                    id uuid not null,
                    occurred_at timestamp with time zone not null,
                    traveler_ref uuid not null,
                    session_id uuid not null,
                    name text not null,
                    props jsonb not null,
                    app_version text not null,
                    platform text not null,
                    constraint "PK_event" primary key (id, occurred_at)
                ) partition by range (occurred_at)
                """);

            migrationBuilder.CreateIndex(
                name: "IX_event_name_occurred_at",
                schema: "insights",
                table: "event",
                columns: new[] { "name", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_event_traveler_ref_occurred_at",
                schema: "insights",
                table: "event",
                columns: new[] { "traveler_ref", "occurred_at" });

            // The partitions of the previous, current and next two months. The service creates the following ones as events arrive and in
            // its daily job (PartitionCatalog); this only means a fresh database accepts events right away.
            migrationBuilder.Sql("""
                do $$
                declare
                    first_day date := date_trunc('month', now() at time zone 'UTC')::date;
                    month_start date;
                begin
                    for offset_months in -1..2 loop
                        month_start := first_day + make_interval(months => offset_months);
                        execute format(
                            'create table if not exists insights.%I partition of insights.event for values from (%L) to (%L)',
                            'event_y' || to_char(month_start, 'YYYY') || 'm' || to_char(month_start, 'MM'),
                            to_char(month_start, 'YYYY-MM-DD') || 'T00:00:00Z',
                            to_char(month_start + interval '1 month', 'YYYY-MM-DD') || 'T00:00:00Z');
                    end loop;
                end
                $$
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "config_snapshot",
                schema: "insights");

            migrationBuilder.DropTable(
                name: "consent_projection",
                schema: "insights");

            migrationBuilder.DropTable(
                name: "daily_kpi",
                schema: "insights");

            migrationBuilder.DropTable(
                name: "event",
                schema: "insights");
        }
    }
}

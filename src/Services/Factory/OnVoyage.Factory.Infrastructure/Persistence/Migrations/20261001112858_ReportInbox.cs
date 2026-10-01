using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handled_at",
                schema: "factory",
                table: "story_report",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolution",
                schema: "factory",
                table: "story_report",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "factory",
                table: "story_report",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_story_report_status_created_at",
                schema: "factory",
                table: "story_report",
                columns: new[] { "status", "created_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_story_report_status",
                schema: "factory",
                table: "story_report",
                sql: "status in ('Open', 'Handled', 'Dismissed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_story_report_status_created_at",
                schema: "factory",
                table: "story_report");

            migrationBuilder.DropCheckConstraint(
                name: "ck_story_report_status",
                schema: "factory",
                table: "story_report");

            migrationBuilder.DropColumn(
                name: "handled_at",
                schema: "factory",
                table: "story_report");

            migrationBuilder.DropColumn(
                name: "resolution",
                schema: "factory",
                table: "story_report");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "factory",
                table: "story_report");
        }
    }
}

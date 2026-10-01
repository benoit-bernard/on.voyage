using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit",
                schema: "platform",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_at",
                schema: "platform",
                table: "admin_audit",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_service_at",
                schema: "platform",
                table: "admin_audit",
                columns: new[] { "service", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit",
                schema: "platform");
        }
    }
}

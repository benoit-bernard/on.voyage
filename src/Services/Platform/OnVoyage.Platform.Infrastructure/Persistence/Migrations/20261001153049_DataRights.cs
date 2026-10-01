using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OnVoyage.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_ack",
                schema: "platform",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_ack", x => new { x.traveler_id, x.service });
                });

            migrationBuilder.CreateTable(
                name: "deletion_log",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    service_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deletion_request",
                schema: "platform",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    required_services = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_request", x => x.traveler_id);
                });

            migrationBuilder.CreateTable(
                name: "export_part",
                schema: "platform",
                columns: table => new
                {
                    export_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_part", x => new { x.export_id, x.service });
                });

            migrationBuilder.CreateTable(
                name: "export_request",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    required_services = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_request", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_export_request_expires_at",
                schema: "platform",
                table: "export_request",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_export_request_traveler_id",
                schema: "platform",
                table: "export_request",
                column: "traveler_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_ack",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "deletion_log",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "deletion_request",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "export_part",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "export_request",
                schema: "platform");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "consent",
                schema: "platform",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    granted = table.Column<bool>(type: "boolean", nullable: false),
                    text_version = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent", x => new { x.traveler_id, x.kind });
                    table.CheckConstraint("ck_consent_kind", "kind in ('analytics', 'ads_personalization')");
                });

            migrationBuilder.CreateTable(
                name: "feature_flag",
                schema: "platform",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    rollout_percent = table.Column<short>(type: "smallint", nullable: false),
                    platforms = table.Column<string[]>(type: "text[]", nullable: false),
                    min_app_version = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_flag", x => x.name);
                    table.CheckConstraint("ck_feature_flag_rollout", "rollout_percent between 0 and 100");
                });

            migrationBuilder.CreateTable(
                name: "remote_config",
                schema: "platform",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_config", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "remote_config_history",
                schema: "platform",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_config_history", x => new { x.key, x.version });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "feature_flag",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "remote_config",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "remote_config_history",
                schema: "platform");
        }
    }
}

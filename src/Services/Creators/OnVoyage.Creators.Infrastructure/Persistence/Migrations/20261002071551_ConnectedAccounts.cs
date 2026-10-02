using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Creators.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "connected_account_id",
                schema: "creators",
                table: "content_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "connected_account",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    external_user_id = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    access_token_protected = table.Column<string>(type: "text", nullable: true),
                    refresh_token_protected = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connected_account", x => x.id);
                    table.CheckConstraint("ck_connected_account_platform", "platform in ('instagram', 'youtube', 'tiktok')");
                    table.CheckConstraint("ck_connected_account_status", "status in ('active', 'needs_reauth')");
                    table.ForeignKey(
                        name: "FK_connected_account_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_item_connected_account_id",
                schema: "creators",
                table: "content_item",
                column: "connected_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_connected_account_last_sync_at",
                schema: "creators",
                table: "connected_account",
                column: "last_sync_at");

            migrationBuilder.CreateIndex(
                name: "ux_connected_account_creator_platform",
                schema: "creators",
                table: "connected_account",
                columns: new[] { "creator_id", "platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_connected_account_platform_external_user",
                schema: "creators",
                table: "connected_account",
                columns: new[] { "platform", "external_user_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_content_item_connected_account_connected_account_id",
                schema: "creators",
                table: "content_item",
                column: "connected_account_id",
                principalSchema: "creators",
                principalTable: "connected_account",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_content_item_connected_account_connected_account_id",
                schema: "creators",
                table: "content_item");

            migrationBuilder.DropTable(
                name: "connected_account",
                schema: "creators");

            migrationBuilder.DropIndex(
                name: "IX_content_item_connected_account_id",
                schema: "creators",
                table: "content_item");

            migrationBuilder.DropColumn(
                name: "connected_account_id",
                schema: "creators",
                table: "content_item");
        }
    }
}

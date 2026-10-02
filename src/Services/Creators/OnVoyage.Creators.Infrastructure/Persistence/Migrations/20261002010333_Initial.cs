using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Creators.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "creators");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "creator",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handle = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    terms_document_ref = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    bio = table.Column<string>(type: "text", nullable: true),
                    avatar_path = table.Column<string>(type: "text", nullable: true),
                    languages = table.Column<string[]>(type: "text[]", nullable: false),
                    specialties = table.Column<string[]>(type: "text[]", nullable: false),
                    destination_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    links = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    terms_version = table.Column<string>(type: "text", nullable: true),
                    terms_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    founding = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator", x => x.id);
                    table.CheckConstraint("ck_creator_status", "status in ('draft', 'published', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "moderation_case",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    reporter_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: true),
                    statement_of_reasons = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_case", x => x.id);
                    table.CheckConstraint("ck_moderation_case_status", "status in ('open', 'decided')");
                    table.CheckConstraint("ck_moderation_case_target", "target_type in ('creator', 'content', 'place_link', 'tip')");
                });

            migrationBuilder.CreateTable(
                name: "poi_directory",
                schema: "creators",
                columns: table => new
                {
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    names = table.Column<string>(type: "jsonb", nullable: false),
                    city = table.Column<string>(type: "text", nullable: true),
                    importance_score = table.Column<short>(type: "smallint", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poi_directory", x => x.poi_id);
                });

            migrationBuilder.CreateTable(
                name: "content_item",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    permalink = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    caption_excerpt = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_s = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    cover_path = table.Column<string>(type: "text", nullable: true),
                    chapters = table.Column<string>(type: "jsonb", nullable: false),
                    is_commercial = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_item", x => x.id);
                    table.CheckConstraint("ck_content_item_excerpt", "char_length(caption_excerpt) <= 500");
                    table.CheckConstraint("ck_content_item_kind", "kind in ('video', 'photo', 'carousel', 'article')");
                    table.CheckConstraint("ck_content_item_platform", "platform in ('instagram', 'youtube', 'tiktok')");
                    table.CheckConstraint("ck_content_item_status", "status in ('imported', 'hidden', 'removed')");
                    table.ForeignKey(
                        name: "FK_content_item_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_tip",
                schema: "creators",
                columns: table => new
                {
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_tip", x => new { x.creator_id, x.poi_id });
                    table.CheckConstraint("ck_creator_tip_status", "status in ('published', 'hidden')");
                    table.CheckConstraint("ck_creator_tip_text", "char_length(\"text\") <= 280");
                    table.ForeignKey(
                        name: "FK_creator_tip_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "follow",
                schema: "creators",
                columns: table => new
                {
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follow", x => new { x.traveler_id, x.creator_id });
                    table.ForeignKey(
                        name: "FK_follow_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "place_link",
                schema: "creators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_id = table.Column<Guid>(type: "uuid", nullable: true),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_s = table.Column<int>(type: "integer", nullable: true),
                    confidence = table.Column<float>(type: "real", nullable: false),
                    signals = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_place_link", x => x.id);
                    table.CheckConstraint("ck_place_link_status", "status in ('proposed', 'validated', 'rejected')");
                    table.ForeignKey(
                        name: "FK_place_link_content_item_content_id",
                        column: x => x.content_id,
                        principalSchema: "creators",
                        principalTable: "content_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_place_link_creator_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "creators",
                        principalTable: "creator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_item_creator_id",
                schema: "creators",
                table: "content_item",
                column: "creator_id");

            migrationBuilder.CreateIndex(
                name: "ux_content_item_platform_external_id",
                schema: "creators",
                table: "content_item",
                columns: new[] { "platform", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_creator_status",
                schema: "creators",
                table: "creator",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_creator_account",
                schema: "creators",
                table: "creator",
                column: "account_id",
                unique: true,
                filter: "account_id is not null");

            migrationBuilder.CreateIndex(
                name: "ux_creator_handle",
                schema: "creators",
                table: "creator",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_creator_tip_id",
                schema: "creators",
                table: "creator_tip",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_follow_creator_id",
                schema: "creators",
                table: "follow",
                column: "creator_id");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_case_reporter_ref",
                schema: "creators",
                table: "moderation_case",
                column: "reporter_ref");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_case_status_created_at",
                schema: "creators",
                table: "moderation_case",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_case_target_type_target_id",
                schema: "creators",
                table: "moderation_case",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "IX_place_link_content_id",
                schema: "creators",
                table: "place_link",
                column: "content_id");

            migrationBuilder.CreateIndex(
                name: "IX_place_link_creator_id_poi_id_content_id_start_s",
                schema: "creators",
                table: "place_link",
                columns: new[] { "creator_id", "poi_id", "content_id", "start_s" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_place_link_poi_id_status",
                schema: "creators",
                table: "place_link",
                columns: new[] { "poi_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_poi_directory_destination_slug",
                schema: "creators",
                table: "poi_directory",
                column: "destination_slug");

            migrationBuilder.CreateIndex(
                name: "IX_poi_directory_search_text",
                schema: "creators",
                table: "poi_directory",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "creator_tip",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "follow",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "moderation_case",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "place_link",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "poi_directory",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "content_item",
                schema: "creators");

            migrationBuilder.DropTable(
                name: "creator",
                schema: "creators");
        }
    }
}

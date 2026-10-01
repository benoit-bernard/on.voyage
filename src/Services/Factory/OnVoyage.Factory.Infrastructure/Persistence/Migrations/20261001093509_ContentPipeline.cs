using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnVoyage.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    quote = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact", x => x.id);
                    table.CheckConstraint("ck_fact_confidence", "confidence between 0 and 1");
                });

            migrationBuilder.CreateTable(
                name: "llm_call",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_id = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    content_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<double>(type: "double precision", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_call", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pronunciation",
                schema: "factory",
                columns: table => new
                {
                    destination_slug = table.Column<string>(type: "text", nullable: false),
                    term = table.Column<string>(type: "text", nullable: false),
                    replacement = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pronunciation", x => new { x.destination_slug, x.term });
                });

            migrationBuilder.CreateTable(
                name: "source_document",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: true),
                    license = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<string>(type: "text", nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    quality = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "story",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lang = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    hook = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    remote_intro = table.Column<string>(type: "text", nullable: false),
                    announce_front = table.Column<string>(type: "text", nullable: false),
                    announce_left = table.Column<string>(type: "text", nullable: false),
                    announce_right = table.Column<string>(type: "text", nullable: false),
                    care_note = table.Column<string>(type: "text", nullable: true),
                    facts_used = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    estimated_duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    quality_score = table.Column<double>(type: "double precision", nullable: false),
                    check_report = table.Column<string>(type: "jsonb", nullable: false),
                    voice_id = table.Column<string>(type: "text", nullable: false),
                    editorial_score = table.Column<double>(type: "double precision", nullable: false),
                    rejected_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "story_audio_part",
                schema: "factory",
                columns: table => new
                {
                    story_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_audio_part", x => new { x.story_id, x.part });
                });

            migrationBuilder.CreateTable(
                name: "story_report",
                schema: "factory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    story_id = table.Column<Guid>(type: "uuid", nullable: false),
                    traveler_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_report", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fact_document_id",
                schema: "factory",
                table: "fact",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_fact_place_id",
                schema: "factory",
                table: "fact",
                column: "place_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_call_created_at",
                schema: "factory",
                table: "llm_call",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_document_place_id_url",
                schema: "factory",
                table: "source_document",
                columns: new[] { "place_id", "url" });

            migrationBuilder.CreateIndex(
                name: "IX_story_place_id_lang_kind_version",
                schema: "factory",
                table: "story",
                columns: new[] { "place_id", "lang", "kind", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_story_report_story_id_traveler_id",
                schema: "factory",
                table: "story_report",
                columns: new[] { "story_id", "traveler_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_story_report_traveler_id_created_at",
                schema: "factory",
                table: "story_report",
                columns: new[] { "traveler_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fact",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "llm_call",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "pronunciation",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "source_document",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "story",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "story_audio_part",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "story_report",
                schema: "factory");
        }
    }
}

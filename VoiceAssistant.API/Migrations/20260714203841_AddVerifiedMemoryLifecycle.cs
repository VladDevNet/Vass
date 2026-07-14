using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedMemoryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CapabilitySnapshotJson",
                table: "Messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SupersedesMemoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    LegacyMemoryFactId = table.Column<int>(type: "integer", nullable: true),
                    SourceMessageId = table.Column<int>(type: "integer", nullable: true),
                    TombstonedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Embedding = table.Column<Vector>(type: "vector(768)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EmbeddingState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRecalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecallCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryItems_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ResultCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MemoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmationTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConfirmationExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_Embedding",
                table: "MemoryItems",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_LegacyMemoryFactId",
                table: "MemoryItems",
                column: "LegacyMemoryFactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_UserId_ContentHash",
                table: "MemoryItems",
                columns: new[] { "UserId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_UserId_Status_UpdatedAt",
                table: "MemoryItems",
                columns: new[] { "UserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_UserId_CreatedAt",
                table: "MemoryOperations",
                columns: new[] { "UserId", "CreatedAt" });

            // Copy, never delete: legacy MemoryFacts remains the compatibility
            // source while callers move to UUID-based MemoryItems. The UUID is
            // deterministic from the legacy integer so a restore/replay keeps
            // the same mapping without requiring another metadata table.
            migrationBuilder.Sql("""
                INSERT INTO "MemoryItems" (
                    "Id", "UserId", "Kind", "Text", "ContentHash", "Status", "Revision",
                    "LegacyMemoryFactId", "SourceMessageId", "TombstonedAt", "Embedding",
                    "EmbeddingModel", "EmbeddingState", "CreatedAt", "UpdatedAt",
                    "LastRecalledAt", "RecallCount")
                SELECT
                    (
                        substring(md5('legacy-memory-fact:' || "Id"::text), 1, 8) || '-' ||
                        substring(md5('legacy-memory-fact:' || "Id"::text), 9, 4) || '-' ||
                        substring(md5('legacy-memory-fact:' || "Id"::text), 13, 4) || '-' ||
                        substring(md5('legacy-memory-fact:' || "Id"::text), 17, 4) || '-' ||
                        substring(md5('legacy-memory-fact:' || "Id"::text), 21, 12)
                    )::uuid,
                    "UserId", 'semantic_fact', "Fact", "ContentHash",
                    CASE WHEN "IsActive" THEN 'active' ELSE 'tombstoned' END,
                    1, "Id", "SourceMessageId",
                    CASE WHEN "IsActive" THEN NULL ELSE "UpdatedAt" END,
                    "Embedding", "EmbeddingModel", 'ready', "CreatedAt", "UpdatedAt",
                    "LastRecalledAt", "RecallCount"
                FROM "MemoryFacts"
                ON CONFLICT ("LegacyMemoryFactId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryItems");

            migrationBuilder.DropTable(
                name: "MemoryOperations");

            migrationBuilder.DropColumn(
                name: "CapabilitySnapshotJson",
                table: "Messages");
        }
    }
}

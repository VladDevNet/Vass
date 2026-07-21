using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCapabilityDiscoveryProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCapabilityDiscoveryConsideredAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CapabilityDiscoveryProgresses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UsageCount = table.Column<int>(type: "integer", nullable: false),
                    FirstUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SuggestionCount = table.Column<int>(type: "integer", nullable: false),
                    FirstSuggestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuggestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingResponseSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapabilityDiscoveryProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapabilityDiscoveryProgresses_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityDiscoveryProgresses_UserId_CapabilityId",
                table: "CapabilityDiscoveryProgresses",
                columns: new[] { "UserId", "CapabilityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityDiscoveryProgresses_UserId_LastSuggestedAt",
                table: "CapabilityDiscoveryProgresses",
                columns: new[] { "UserId", "LastSuggestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapabilityDiscoveryProgresses");

            migrationBuilder.DropColumn(
                name: "LastCapabilityDiscoveryConsideredAt",
                table: "AspNetUsers");
        }
    }
}

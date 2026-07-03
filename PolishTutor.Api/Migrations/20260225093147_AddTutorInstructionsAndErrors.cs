using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PolishTutor.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTutorInstructionsAndErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LearnerErrors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ChatSessionId = table.Column<int>(type: "integer", nullable: false),
                    ErrorType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Original = table.Column<string>(type: "text", nullable: false),
                    Corrected = table.Column<string>(type: "text", nullable: false),
                    GrammarTopic = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnerErrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LearnerErrors_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LearnerErrors_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TutorInstructions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    InstructionsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TutorInstructions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TutorInstructions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LearnerErrors_ChatSessionId",
                table: "LearnerErrors",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LearnerErrors_UserId",
                table: "LearnerErrors",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TutorInstructions_UserId",
                table: "TutorInstructions",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearnerErrors");

            migrationBuilder.DropTable(
                name: "TutorInstructions");
        }
    }
}

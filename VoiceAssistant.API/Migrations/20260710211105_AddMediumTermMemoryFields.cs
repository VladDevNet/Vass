using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddMediumTermMemoryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastSummarizedMessageId",
                table: "ChatSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediumTermSummary",
                table: "ChatSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSummarizedMessageId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "MediumTermSummary",
                table: "ChatSessions");
        }
    }
}

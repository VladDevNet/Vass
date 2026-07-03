using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolishTutor.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeminiApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GeminiApiKey",
                table: "UserSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeminiApiKey",
                table: "UserSettings");
        }
    }
}

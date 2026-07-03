using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolishTutor.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFullTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FullTranslation",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullTranslation",
                table: "UserSettings");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolishTutor.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAudioFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFileName",
                table: "Messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFileName",
                table: "Messages");
        }
    }
}

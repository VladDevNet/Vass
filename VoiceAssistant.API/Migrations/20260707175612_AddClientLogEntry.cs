using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddClientLogEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientLogEntries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientLogEntries_RunId",
                table: "ClientLogEntries",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientLogEntries_UserId_ClientTimestamp",
                table: "ClientLogEntries",
                columns: new[] { "UserId", "ClientTimestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientLogEntries");
        }
    }
}

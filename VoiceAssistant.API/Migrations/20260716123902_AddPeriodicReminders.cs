using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddPeriodicReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "Reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "Reminders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_UserId_OperationId",
                table: "Reminders",
                columns: new[] { "UserId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reminders_UserId_OperationId",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "Reminders");
        }
    }
}

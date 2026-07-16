using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VoiceAssistant.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredMemoryAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "MemoryItems",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "other");

            migrationBuilder.AddColumn<Guid>(
                name: "VisualAssetId",
                table: "MemoryItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_UserId_Category_Status_UpdatedAt",
                table: "MemoryItems",
                columns: new[] { "UserId", "Category", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryItems_VisualAssetId",
                table: "MemoryItems",
                column: "VisualAssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_MemoryItems_VisualAssets_VisualAssetId",
                table: "MemoryItems",
                column: "VisualAssetId",
                principalTable: "VisualAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MemoryItems_VisualAssets_VisualAssetId",
                table: "MemoryItems");

            migrationBuilder.DropIndex(
                name: "IX_MemoryItems_UserId_Category_Status_UpdatedAt",
                table: "MemoryItems");

            migrationBuilder.DropIndex(
                name: "IX_MemoryItems_VisualAssetId",
                table: "MemoryItems");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "MemoryItems");

            migrationBuilder.DropColumn(
                name: "VisualAssetId",
                table: "MemoryItems");
        }
    }
}

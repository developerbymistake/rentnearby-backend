using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatBlockAndQuestionTargeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlotTypeId",
                table: "QuestionTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoomTypeId",
                table: "QuestionTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RespondsToMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionTemplates_PlotTypeId",
                table: "QuestionTemplates",
                column: "PlotTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionTemplates_RoomTypeId",
                table: "QuestionTemplates",
                column: "RoomTypeId");

            migrationBuilder.CreateIndex(
                name: "ix_messages_responds_to_unique",
                table: "Messages",
                column: "RespondsToMessageId",
                unique: true,
                filter: "\"RespondsToMessageId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Messages_RespondsToMessageId",
                table: "Messages",
                column: "RespondsToMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QuestionTemplates_PlotTypes_PlotTypeId",
                table: "QuestionTemplates",
                column: "PlotTypeId",
                principalTable: "PlotTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QuestionTemplates_RoomTypes_RoomTypeId",
                table: "QuestionTemplates",
                column: "RoomTypeId",
                principalTable: "RoomTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Messages_RespondsToMessageId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_QuestionTemplates_PlotTypes_PlotTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_QuestionTemplates_RoomTypes_RoomTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropIndex(
                name: "IX_QuestionTemplates_PlotTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropIndex(
                name: "IX_QuestionTemplates_RoomTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropIndex(
                name: "ix_messages_responds_to_unique",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PlotTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropColumn(
                name: "RoomTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropColumn(
                name: "RespondsToMessageId",
                table: "Messages");
        }
    }
}

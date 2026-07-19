using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentUserIdAndStatusHistoryAgentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChangedByAgentId",
                table: "InquiryStatusHistories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "Agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InquiryStatusHistories_ChangedByAgentId",
                table: "InquiryStatusHistories",
                column: "ChangedByAgentId");

            migrationBuilder.CreateIndex(
                name: "ix_agents_userid_unique",
                table: "Agents",
                column: "UserId",
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Users_UserId",
                table: "Agents",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_InquiryStatusHistories_Agents_ChangedByAgentId",
                table: "InquiryStatusHistories",
                column: "ChangedByAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Users_UserId",
                table: "Agents");

            migrationBuilder.DropForeignKey(
                name: "FK_InquiryStatusHistories_Agents_ChangedByAgentId",
                table: "InquiryStatusHistories");

            migrationBuilder.DropIndex(
                name: "IX_InquiryStatusHistories_ChangedByAgentId",
                table: "InquiryStatusHistories");

            migrationBuilder.DropIndex(
                name: "ix_agents_userid_unique",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ChangedByAgentId",
                table: "InquiryStatusHistories");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Agents");
        }
    }
}

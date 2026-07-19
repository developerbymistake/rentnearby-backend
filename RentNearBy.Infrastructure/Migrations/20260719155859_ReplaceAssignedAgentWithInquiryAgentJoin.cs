using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAssignedAgentWithInquiryAgentJoin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Inquiries_Agents_AssignedAgentId",
                table: "Inquiries");

            migrationBuilder.DropIndex(
                name: "IX_Inquiries_AssignedAgentId",
                table: "Inquiries");

            migrationBuilder.DropColumn(
                name: "AssignedAgentId",
                table: "Inquiries");

            migrationBuilder.CreateTable(
                name: "InquiryAgents",
                columns: table => new
                {
                    InquiryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InquiryAgents", x => new { x.InquiryId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_InquiryAgents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InquiryAgents_Inquiries_InquiryId",
                        column: x => x.InquiryId,
                        principalTable: "Inquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InquiryAgents_AgentId",
                table: "InquiryAgents",
                column: "AgentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InquiryAgents");

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedAgentId",
                table: "Inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inquiries_AssignedAgentId",
                table: "Inquiries",
                column: "AssignedAgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Inquiries_Agents_AssignedAgentId",
                table: "Inquiries",
                column: "AssignedAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

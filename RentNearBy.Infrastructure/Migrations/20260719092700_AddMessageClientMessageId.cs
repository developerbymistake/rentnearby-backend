using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageClientMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_client_message_id_unique",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ClientMessageId" },
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_client_message_id_unique",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ClientMessageId",
                table: "Messages");
        }
    }
}

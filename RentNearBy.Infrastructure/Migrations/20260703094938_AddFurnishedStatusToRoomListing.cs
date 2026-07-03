using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFurnishedStatusToRoomListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RoomMemberships_Users_UserId1",
                table: "RoomMemberships");

            migrationBuilder.DropIndex(
                name: "IX_RoomMemberships_UserId1",
                table: "RoomMemberships");

            migrationBuilder.DropColumn(
                name: "UserId1",
                table: "RoomMemberships");

            migrationBuilder.AddColumn<string>(
                name: "FurnishedStatus",
                table: "RoomListings",
                type: "text",
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FurnishedStatus",
                table: "RoomListings");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId1",
                table: "RoomMemberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_UserId1",
                table: "RoomMemberships",
                column: "UserId1");

            migrationBuilder.AddForeignKey(
                name: "FK_RoomMemberships_Users_UserId1",
                table: "RoomMemberships",
                column: "UserId1",
                principalTable: "Users",
                principalColumn: "Id");
        }
    }
}

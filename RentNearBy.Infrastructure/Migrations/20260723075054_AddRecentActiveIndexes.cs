using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecentActiveIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoomListings_CreatedAt",
                table: "RoomListings");

            migrationBuilder.DropIndex(
                name: "IX_PlotListings_CreatedAt",
                table: "PlotListings");

            migrationBuilder.CreateIndex(
                name: "ix_roomlistings_recent_active",
                table: "RoomListings",
                column: "CreatedAt",
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_plotlistings_recent_active",
                table: "PlotListings",
                column: "CreatedAt",
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_roomlistings_recent_active",
                table: "RoomListings");

            migrationBuilder.DropIndex(
                name: "ix_plotlistings_recent_active",
                table: "PlotListings");

            migrationBuilder.CreateIndex(
                name: "IX_RoomListings_CreatedAt",
                table: "RoomListings",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlotListings_CreatedAt",
                table: "PlotListings",
                column: "CreatedAt");
        }
    }
}

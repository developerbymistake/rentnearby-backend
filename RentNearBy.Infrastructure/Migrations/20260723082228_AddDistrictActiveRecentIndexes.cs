using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistrictActiveRecentIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_roomlistings_district_active_recent",
                table: "RoomListings",
                columns: new[] { "DistrictId", "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_plotlistings_district_active_recent",
                table: "PlotListings",
                columns: new[] { "DistrictId", "IsActive", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_roomlistings_district_active_recent",
                table: "RoomListings");

            migrationBuilder.DropIndex(
                name: "ix_plotlistings_district_active_recent",
                table: "PlotListings");
        }
    }
}

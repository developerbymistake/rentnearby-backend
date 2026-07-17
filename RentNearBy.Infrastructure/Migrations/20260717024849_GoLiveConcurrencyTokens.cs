using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GoLiveConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "RoomListings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "PlotListings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "PlotListings");
        }
    }
}

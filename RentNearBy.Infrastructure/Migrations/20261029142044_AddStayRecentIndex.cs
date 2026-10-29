using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStayRecentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_staylistings_recent_active",
                table: "StayListings",
                column: "CreatedAt",
                descending: new bool[0],
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_staylistings_recent_active",
                table: "StayListings");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestNotifiedAtToRoomAndPlotListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DigestNotifiedAt",
                table: "RoomListings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DigestNotifiedAt",
                table: "PlotListings",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill pre-existing rows so the first digest run doesn't report all
            // historical listings as "new". Both columns are freshly added (all-NULL
            // at this point), so the WHERE IS NULL clause is a safety no-op guard
            // rather than a meaningful filter.
            migrationBuilder.Sql(
                "UPDATE \"RoomListings\" SET \"DigestNotifiedAt\" = NOW() WHERE \"DigestNotifiedAt\" IS NULL;");
            migrationBuilder.Sql(
                "UPDATE \"PlotListings\" SET \"DigestNotifiedAt\" = NOW() WHERE \"DigestNotifiedAt\" IS NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_roomlistings_digest_pending",
                table: "RoomListings",
                columns: new[] { "IsActive", "IsDeleted", "DigestNotifiedAt", "DistrictId" },
                filter: "\"DigestNotifiedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_plotlistings_digest_pending",
                table: "PlotListings",
                columns: new[] { "IsActive", "IsDeleted", "DigestNotifiedAt", "DistrictId" },
                filter: "\"DigestNotifiedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_roomlistings_digest_pending",
                table: "RoomListings");

            migrationBuilder.DropIndex(
                name: "ix_plotlistings_digest_pending",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "DigestNotifiedAt",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "DigestNotifiedAt",
                table: "PlotListings");
        }
    }
}

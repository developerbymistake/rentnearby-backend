using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinPackAndListingLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoinPacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Coins = table.Column<int>(type: "integer", nullable: false),
                    BonusCoins = table.Column<int>(type: "integer", nullable: false),
                    PriceInr = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsFeatured = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinPacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListingLimitSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ListingKind = table.Column<string>(type: "text", nullable: false),
                    MaxListings = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingLimitSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingLimitSettings_ListingKind",
                table: "ListingLimitSettings",
                column: "ListingKind",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoinPacks");

            migrationBuilder.DropTable(
                name: "ListingLimitSettings");
        }
    }
}

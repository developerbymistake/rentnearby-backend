using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinPackPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoinPackPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoinPackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Coins = table.Column<int>(type: "integer", nullable: false),
                    BonusCoins = table.Column<int>(type: "integer", nullable: false),
                    PriceInr = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RazorpayOrderId = table.Column<string>(type: "text", nullable: true),
                    RazorpayPaymentId = table.Column<string>(type: "text", nullable: true),
                    RazorpaySignature = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinPackPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinPackPurchases_CoinPacks_CoinPackId",
                        column: x => x.CoinPackId,
                        principalTable: "CoinPacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoinPackPurchases_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoinPackPurchases_CoinPackId",
                table: "CoinPackPurchases",
                column: "CoinPackId");

            migrationBuilder.CreateIndex(
                name: "ix_coinpackpurchases_pending_user",
                table: "CoinPackPurchases",
                column: "UserId",
                unique: true,
                filter: "\"Status\" = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_CoinPackPurchases_RazorpayOrderId",
                table: "CoinPackPurchases",
                column: "RazorpayOrderId");

            migrationBuilder.CreateIndex(
                name: "ix_coinpackpurchases_user",
                table: "CoinPackPurchases",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoinPackPurchases");
        }
    }
}

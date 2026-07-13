using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionPendingUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_PlotId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_RoomListingId",
                table: "PaymentTransactions");

            migrationBuilder.CreateIndex(
                name: "ix_paymenttransactions_pending_plot_listing",
                table: "PaymentTransactions",
                column: "PlotId",
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"PlotId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_paymenttransactions_pending_plot_upgrade",
                table: "PaymentTransactions",
                columns: new[] { "UserId", "PlanType" },
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"RoomListingId\" IS NULL AND \"PlotId\" IS NULL AND \"TransactionKind\" = 'PLOT'");

            migrationBuilder.CreateIndex(
                name: "ix_paymenttransactions_pending_room_listing",
                table: "PaymentTransactions",
                column: "RoomListingId",
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"RoomListingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_paymenttransactions_pending_room_upgrade",
                table: "PaymentTransactions",
                columns: new[] { "UserId", "PlanType" },
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"RoomListingId\" IS NULL AND \"PlotId\" IS NULL AND \"TransactionKind\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_paymenttransactions_pending_plot_listing",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "ix_paymenttransactions_pending_plot_upgrade",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "ix_paymenttransactions_pending_room_listing",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "ix_paymenttransactions_pending_room_upgrade",
                table: "PaymentTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_PlotId",
                table: "PaymentTransactions",
                column: "PlotId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_RoomListingId",
                table: "PaymentTransactions",
                column: "RoomListingId");
        }
    }
}

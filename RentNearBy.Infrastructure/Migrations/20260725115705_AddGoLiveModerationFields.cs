using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGoLiveModerationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credittransactions_oneshot_unique",
                table: "CreditTransactions");

            migrationBuilder.AddColumn<string>(
                name: "LiveRequestStatus",
                table: "RoomListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedReason",
                table: "RoomListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedPlanCreditsSpent",
                table: "RoomListings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedPlanDays",
                table: "RoomListings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedPlanType",
                table: "RoomListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveRequestStatus",
                table: "PlotListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedReason",
                table: "PlotListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedPlanCreditsSpent",
                table: "PlotListings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedPlanDays",
                table: "PlotListings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedPlanType",
                table: "PlotListings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_credittransactions_oneshot_unique",
                table: "CreditTransactions",
                columns: new[] { "UserId", "Reason", "ReferenceId" },
                unique: true,
                filter: "\"Reason\" IN ('RECHARGE', 'COUPON_REDEEM', 'WELCOME_BONUS', 'ADMIN_CREDIT', 'GOLIVE_PENDING_REFUND', 'ADMIN_DEBIT') AND \"ReferenceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credittransactions_oneshot_unique",
                table: "CreditTransactions");

            migrationBuilder.DropColumn(
                name: "LiveRequestStatus",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "RejectedReason",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanCreditsSpent",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanDays",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanType",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "LiveRequestStatus",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "RejectedReason",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanCreditsSpent",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanDays",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "RequestedPlanType",
                table: "PlotListings");

            migrationBuilder.CreateIndex(
                name: "ix_credittransactions_oneshot_unique",
                table: "CreditTransactions",
                columns: new[] { "UserId", "Reason", "ReferenceId" },
                unique: true,
                filter: "\"Reason\" IN ('RECHARGE', 'COUPON_REDEEM', 'WELCOME_BONUS', 'ADMIN_CREDIT', 'ADMIN_DEBIT') AND \"ReferenceId\" IS NOT NULL");
        }
    }
}

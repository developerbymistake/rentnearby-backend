using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOldMembershipSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppFeatures");

            migrationBuilder.DropTable(
                name: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "PlotMemberships");

            migrationBuilder.DropTable(
                name: "RoomMemberships");

            migrationBuilder.DropColumn(
                name: "HasUsedFreePlan",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HasUsedFreePlotPlan",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasUsedFreePlan",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasUsedFreePlotPlan",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AppFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    FreeDays = table.Column<int>(type: "integer", nullable: false),
                    FreeLimit = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppFeatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlotId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoomListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    RazorpayOrderId = table.Column<string>(type: "text", nullable: true),
                    RazorpayPaymentId = table.Column<string>(type: "text", nullable: true),
                    RazorpaySignature = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TransactionKind = table.Column<string>(type: "text", nullable: true),
                    UserId1 = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_PlotListings_PlotId",
                        column: x => x.PlotId,
                        principalTable: "PlotListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_RoomListings_RoomListingId",
                        column: x => x.RoomListingId,
                        principalTable: "RoomListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId1",
                        column: x => x.UserId1,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PlotMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MaxPlotListings = table.Column<int>(type: "integer", nullable: false),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlotMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoomMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MaxRooms = table.Column<int>(type: "integer", nullable: false),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoomMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppFeatures_Key",
                table: "AppFeatures",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_CreatedAt",
                table: "PaymentTransactions",
                column: "CreatedAt");

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

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_RazorpayOrderId",
                table: "PaymentTransactions",
                column: "RazorpayOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_Status",
                table: "PaymentTransactions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_UserId",
                table: "PaymentTransactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_UserId1",
                table: "PaymentTransactions",
                column: "UserId1");

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_IsActive",
                table: "PlotMemberships",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_IsActive_ValidUntil",
                table: "PlotMemberships",
                columns: new[] { "IsActive", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_UserId",
                table: "PlotMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_UserId_IsActive",
                table: "PlotMemberships",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_ValidUntil",
                table: "PlotMemberships",
                column: "ValidUntil");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_IsActive",
                table: "RoomMemberships",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_IsActive_ValidUntil",
                table: "RoomMemberships",
                columns: new[] { "IsActive", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_UserId",
                table: "RoomMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_UserId_IsActive",
                table: "RoomMemberships",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomMemberships_ValidUntil",
                table: "RoomMemberships",
                column: "ValidUntil");
        }
    }
}

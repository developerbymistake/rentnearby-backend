using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlotPaymentSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasUsedFreePlotPlan",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntil",
                table: "Plots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlotId",
                table: "PaymentTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlotPaymentFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotPaymentFeatures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_PlotId",
                table: "PaymentTransactions",
                column: "PlotId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_Plots_PlotId",
                table: "PaymentTransactions",
                column: "PlotId",
                principalTable: "Plots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_Plots_PlotId",
                table: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "PlotPaymentFeatures");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_PlotId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "HasUsedFreePlotPlan",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "Plots");

            migrationBuilder.DropColumn(
                name: "PlotId",
                table: "PaymentTransactions");
        }
    }
}

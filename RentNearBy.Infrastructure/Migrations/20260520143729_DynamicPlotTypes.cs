using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DynamicPlotTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create PlotTypes table
            migrationBuilder.CreateTable(
                name: "PlotTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotTypes", x => x.Id);
                });

            // 2. Seed the 3 initial plot types with fixed GUIDs
            migrationBuilder.InsertData(
                table: "PlotTypes",
                columns: new[] { "Id", "CreatedAt", "Description", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("b1000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Residential", 1 },
                    { new Guid("b1000000-0000-0000-0000-000000000002"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Commercial", 2 },
                    { new Guid("b1000000-0000-0000-0000-000000000003"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Agricultural", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlotTypes_Name",
                table: "PlotTypes",
                column: "Name",
                unique: true);

            // 3. Add PlotTypeId as nullable first so existing rows don't violate NOT NULL
            migrationBuilder.AddColumn<Guid>(
                name: "PlotTypeId",
                table: "Plots",
                type: "uuid",
                nullable: true);

            // 4. Migrate existing string data to FK GUIDs
            migrationBuilder.Sql(@"
                UPDATE ""Plots"" SET ""PlotTypeId"" = CASE ""PlotType""
                    WHEN 'Residential'  THEN 'b1000000-0000-0000-0000-000000000001'::uuid
                    WHEN 'Commercial'   THEN 'b1000000-0000-0000-0000-000000000002'::uuid
                    WHEN 'Agricultural' THEN 'b1000000-0000-0000-0000-000000000003'::uuid
                    ELSE 'b1000000-0000-0000-0000-000000000001'::uuid
                END
            ");

            // 5. Make PlotTypeId NOT NULL now that all rows are populated
            migrationBuilder.AlterColumn<Guid>(
                name: "PlotTypeId",
                table: "Plots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // 6. Add FK constraint
            migrationBuilder.CreateIndex(
                name: "IX_Plots_PlotTypeId",
                table: "Plots",
                column: "PlotTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Plots_PlotTypes_PlotTypeId",
                table: "Plots",
                column: "PlotTypeId",
                principalTable: "PlotTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // 7. Drop the old string column
            migrationBuilder.DropColumn(
                name: "PlotType",
                table: "Plots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Plots_PlotTypes_PlotTypeId",
                table: "Plots");

            migrationBuilder.DropTable(
                name: "PlotTypes");

            migrationBuilder.DropIndex(
                name: "IX_Plots_PlotTypeId",
                table: "Plots");

            migrationBuilder.DropColumn(
                name: "PlotTypeId",
                table: "Plots");

            migrationBuilder.AddColumn<string>(
                name: "PlotType",
                table: "Plots",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseInsensitiveCityNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cities_DistrictId_Name",
                table: "Cities");

            migrationBuilder.AddColumn<string>(
                name: "NameLower",
                table: "Cities",
                type: "text",
                nullable: true,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_cities_district_namelower_unique",
                table: "Cities",
                columns: new[] { "DistrictId", "NameLower" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cities_district_namelower_unique",
                table: "Cities");

            migrationBuilder.DropColumn(
                name: "NameLower",
                table: "Cities");

            migrationBuilder.CreateIndex(
                name: "IX_Cities_DistrictId_Name",
                table: "Cities",
                columns: new[] { "DistrictId", "Name" },
                unique: true);
        }
    }
}

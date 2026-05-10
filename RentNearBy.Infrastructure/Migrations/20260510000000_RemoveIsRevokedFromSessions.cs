using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveIsRevokedFromSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_UserId_IsRevoked",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IsRevoked",
                table: "Sessions");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions");

            migrationBuilder.AddColumn<bool>(
                name: "IsRevoked",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId_IsRevoked",
                table: "Sessions",
                columns: new[] { "UserId", "IsRevoked" });
        }
    }
}

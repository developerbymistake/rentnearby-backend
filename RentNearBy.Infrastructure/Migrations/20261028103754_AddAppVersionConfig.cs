using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppVersionConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppVersionConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinBuildNumber = table.Column<int>(type: "integer", nullable: false),
                    MinVersionName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LatestBuildNumber = table.Column<int>(type: "integer", nullable: false),
                    LatestVersionName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoreUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ForceCacheMaxAgeHours = table.Column<int>(type: "integer", nullable: false),
                    MaxSeenBuild = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppVersionConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppVersionConfigs_Admins_UpdatedByAdminId",
                        column: x => x.UpdatedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppVersionConfigs_Platform",
                table: "AppVersionConfigs",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppVersionConfigs_UpdatedByAdminId",
                table: "AppVersionConfigs",
                column: "UpdatedByAdminId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppVersionConfigs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlotPlanSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Listings_DistrictId_IsActive_CreatedAt",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_Latitude_Longitude",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Cities_Name",
                table: "Cities");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Listings");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<bool>(
                name: "HasUsedFreePlan",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Listings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntil",
                table: "Listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Point>(
                name: "Location",
                table: "Listings",
                type: "geography(Point, 4326)",
                nullable: true,
                computedColumnSql: "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                stored: true);

            migrationBuilder.CreateTable(
                name: "PaymentFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentFeatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    RazorpayOrderId = table.Column<string>(type: "text", nullable: true),
                    RazorpayPaymentId = table.Column<string>(type: "text", nullable: true),
                    RazorpaySignature = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserId1 = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId1",
                        column: x => x.UserId1,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    RoomLimit = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlotMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxPlots = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
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
                name: "PlotPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    PlotLimit = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AreaValue = table.Column<decimal>(type: "numeric", nullable: false),
                    AreaUnit = table.Column<string>(type: "text", nullable: false),
                    AreaSqft = table.Column<decimal>(type: "numeric", nullable: false),
                    PlotType = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: false),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Location = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plots_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Plots_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Plots_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanType = table.Column<string>(type: "text", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxRooms = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UserId1 = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserMemberships_Users_UserId1",
                        column: x => x.UserId1,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PlotPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhotoUrl = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    PhotoOrder = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlotPhotos_Plots_PlotId",
                        column: x => x.PlotId,
                        principalTable: "Plots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_CityId_IsActive",
                table: "Listings",
                columns: new[] { "CityId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_CityId_IsActive_CreatedAt",
                table: "Listings",
                columns: new[] { "CityId", "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_listings_location_gist",
                table: "Listings",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_UserId_CreatedAt",
                table: "Listings",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Cities_DistrictId_Name",
                table: "Cities",
                columns: new[] { "DistrictId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_CreatedAt",
                table: "PaymentTransactions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ListingId",
                table: "PaymentTransactions",
                column: "ListingId");

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
                name: "IX_Plans_PlanType",
                table: "Plans",
                column: "PlanType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_IsActive",
                table: "PlotMemberships",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_UserId",
                table: "PlotMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlotMemberships_ValidUntil",
                table: "PlotMemberships",
                column: "ValidUntil");

            migrationBuilder.CreateIndex(
                name: "IX_PlotPhotos_PlotId",
                table: "PlotPhotos",
                column: "PlotId");

            migrationBuilder.CreateIndex(
                name: "IX_PlotPlans_PlanType",
                table: "PlotPlans",
                column: "PlanType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plots_AreaSqft",
                table: "Plots",
                column: "AreaSqft");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_CityId",
                table: "Plots",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_CityId_IsActive",
                table: "Plots",
                columns: new[] { "CityId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Plots_CityId_IsActive_CreatedAt",
                table: "Plots",
                columns: new[] { "CityId", "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Plots_CreatedAt",
                table: "Plots",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_DistrictId",
                table: "Plots",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_IsActive",
                table: "Plots",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "ix_plots_location_gist",
                table: "Plots",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_UserId",
                table: "Plots",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Plots_UserId_CreatedAt",
                table: "Plots",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserMemberships_IsActive",
                table: "UserMemberships",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserMemberships_UserId",
                table: "UserMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMemberships_UserId1",
                table: "UserMemberships",
                column: "UserId1");

            migrationBuilder.CreateIndex(
                name: "IX_UserMemberships_ValidUntil",
                table: "UserMemberships",
                column: "ValidUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentFeatures");

            migrationBuilder.DropTable(
                name: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "PlotMemberships");

            migrationBuilder.DropTable(
                name: "PlotPhotos");

            migrationBuilder.DropTable(
                name: "PlotPlans");

            migrationBuilder.DropTable(
                name: "UserMemberships");

            migrationBuilder.DropTable(
                name: "Plots");

            migrationBuilder.DropIndex(
                name: "IX_Listings_CityId_IsActive",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_CityId_IsActive_CreatedAt",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "ix_listings_location_gist",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_UserId_CreatedAt",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Cities_DistrictId_Name",
                table: "Cities");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "HasUsedFreePlan",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "Listings");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Listings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Listings_DistrictId_IsActive_CreatedAt",
                table: "Listings",
                columns: new[] { "DistrictId", "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_Latitude_Longitude",
                table: "Listings",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Cities_Name",
                table: "Cities",
                column: "Name");
        }
    }
}

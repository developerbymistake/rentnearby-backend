using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxiVehicleTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultSeats = table.Column<short>(type: "smallint", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiVehicleTypes", x => x.Id);
                    table.CheckConstraint("ck_taxivehicletypes_category", "\"Category\" IN ('Hatchback', 'Sedan', 'SUV', 'MUV', 'TempoTraveller', 'Bus')");
                });

            migrationBuilder.CreateTable(
                name: "TaxiListings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Seats = table.Column<short>(type: "smallint", nullable: true),
                    NumberPlate = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    PlatePhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PlatePhotoPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    WhatsAppNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RejectedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinFare = table.Column<int>(type: "integer", nullable: true),
                    Slug = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Location = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography", stored: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiListings", x => x.Id);
                    table.CheckConstraint("ck_taxilistings_min_fare_range", "\"MinFare\" IS NULL OR (\"MinFare\" >= 100 AND \"MinFare\" <= 9900000)");
                    table.CheckConstraint("ck_taxilistings_plate", "\"NumberPlate\" IS NULL OR \"NumberPlate\" ~ '^([A-Z]{2}[0-9]{1,2}[A-Z]{1,3}[0-9]{4}|[0-9]{2}BH[0-9]{4}[A-Z]{1,2})$'");
                    table.CheckConstraint("ck_taxilistings_seats", "\"Seats\" IS NULL OR \"Seats\" IN (4, 6, 7, 12)");
                    table.CheckConstraint("ck_taxilistings_status", "\"Status\" IN ('Draft', 'PendingReview', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_TaxiListings_Admins_ReviewedByAdminId",
                        column: x => x.ReviewedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaxiListings_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaxiListings_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxiListings_TaxiVehicleTypes_VehicleTypeId",
                        column: x => x.VehicleTypeId,
                        principalTable: "TaxiVehicleTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxiListings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxiRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TaxiListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RouteIndex = table.Column<short>(type: "smallint", nullable: false),
                    FromName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FromLabel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FromLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    FromLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ToLabel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ToLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    Fare = table.Column<int>(type: "integer", nullable: false),
                    TripType = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    FromPoint = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(\"FromLongitude\"::float8, \"FromLatitude\"::float8), 4326)::geography", stored: true),
                    ToPoint = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(\"ToLongitude\"::float8, \"ToLatitude\"::float8), 4326)::geography", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiRoutes", x => x.Id);
                    table.CheckConstraint("ck_taxiroutes_fare_range", "\"Fare\" >= 100 AND \"Fare\" <= 9900000");
                    table.CheckConstraint("ck_taxiroutes_trip_type", "\"TripType\" IN ('OneWay', 'RoundTrip')");
                    table.ForeignKey(
                        name: "FK_TaxiRoutes_TaxiListings_TaxiListingId",
                        column: x => x.TaxiListingId,
                        principalTable: "TaxiListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "Description", "IsActive", "Key", "Name", "SortOrder" },
                values: new object[] { new Guid("10000000-0000-0000-0000-000000000005"), "List your taxi", true, "TAXI_DRIVER", "Taxi Driver", 5 });

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_browse_district_city_fare",
                table: "TaxiListings",
                columns: new[] { "DistrictId", "CityId", "MinFare" },
                filter: "\"Status\" = 'Approved' AND \"IsAvailable\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_browse_district_fare",
                table: "TaxiListings",
                columns: new[] { "DistrictId", "MinFare" },
                filter: "\"Status\" = 'Approved' AND \"IsAvailable\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_CityId",
                table: "TaxiListings",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_DistrictId",
                table: "TaxiListings",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_location_gist",
                table: "TaxiListings",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_plate_live",
                table: "TaxiListings",
                column: "NumberPlate",
                unique: true,
                filter: "\"IsDeleted\" = false AND \"NumberPlate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_public_id",
                table: "TaxiListings",
                column: "Id",
                filter: "\"Status\" = 'Approved' AND \"IsAvailable\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_ReviewedByAdminId",
                table: "TaxiListings",
                column: "ReviewedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_Slug",
                table: "TaxiListings",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_Status_SubmittedAt",
                table: "TaxiListings",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_user_live",
                table: "TaxiListings",
                column: "UserId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_UserId_IsDeleted",
                table: "TaxiListings",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxiListings_VehicleTypeId",
                table: "TaxiListings",
                column: "VehicleTypeId");

            migrationBuilder.CreateIndex(
                name: "ix_taxiroutes_from_gist",
                table: "TaxiRoutes",
                column: "FromPoint")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_TaxiRoutes_TaxiListingId_RouteIndex",
                table: "TaxiRoutes",
                columns: new[] { "TaxiListingId", "RouteIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxiroutes_to_gist",
                table: "TaxiRoutes",
                column: "ToPoint")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_taxivehicletypes_name",
                table: "TaxiVehicleTypes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxiRoutes");

            migrationBuilder.DropTable(
                name: "TaxiListings");

            migrationBuilder.DropTable(
                name: "TaxiVehicleTypes");

            migrationBuilder.DeleteData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"));
        }
    }
}

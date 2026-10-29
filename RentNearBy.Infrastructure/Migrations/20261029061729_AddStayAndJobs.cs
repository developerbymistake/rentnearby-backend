using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStayAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StayTypeId",
                table: "QuestionTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobDepartments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobDepartments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobSalaryTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSalaryTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobShiftTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobShiftTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StayTypes",
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
                    table.PrimaryKey("PK_StayTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    JobDepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRoles_JobDepartments_JobDepartmentId",
                        column: x => x.JobDepartmentId,
                        principalTable: "JobDepartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StayListings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    StayTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    VehicleAccess = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    HostLivesHere = table.Column<bool>(type: "boolean", nullable: true),
                    Facilities = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckInTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    CheckOutTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    PetsAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    SmokingAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    AlcoholAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    UnmarriedCouplesAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    IdProofRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CancellationPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RulesSavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    AcceptingGuests = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RejectedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinPricePerNight = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_StayListings", x => x.Id);
                    table.CheckConstraint("ck_staylistings_min_price_range", "\"MinPricePerNight\" IS NULL OR (\"MinPricePerNight\" >= 100 AND \"MinPricePerNight\" <= 9900000)");
                    table.CheckConstraint("ck_staylistings_status", "\"Status\" IN ('Draft', 'PendingReview', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_StayListings_Admins_ReviewedByAdminId",
                        column: x => x.ReviewedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StayListings_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StayListings_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayListings_StayTypes_StayTypeId",
                        column: x => x.StayTypeId,
                        principalTable: "StayTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayListings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StayJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StayListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    JobDepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobShiftTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobSalaryTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinimumExperienceYears = table.Column<int>(type: "integer", nullable: false),
                    MinSalary = table.Column<int>(type: "integer", nullable: false),
                    MaxSalary = table.Column<int>(type: "integer", nullable: false),
                    ContactPhone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WhatsAppNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeekerPushedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayJobs", x => x.Id);
                    table.CheckConstraint("ck_stayjobs_min_exp", "\"MinimumExperienceYears\" BETWEEN 0 AND 20");
                    table.CheckConstraint("ck_stayjobs_salary", "\"MinSalary\" >= 100 AND \"MaxSalary\" >= \"MinSalary\" AND \"MaxSalary\" <= 9900000");
                    table.CheckConstraint("ck_stayjobs_status", "\"Status\" IN ('Active', 'Closed')");
                    table.ForeignKey(
                        name: "FK_StayJobs_JobDepartments_JobDepartmentId",
                        column: x => x.JobDepartmentId,
                        principalTable: "JobDepartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayJobs_JobRoles_JobRoleId",
                        column: x => x.JobRoleId,
                        principalTable: "JobRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayJobs_JobSalaryTypes_JobSalaryTypeId",
                        column: x => x.JobSalaryTypeId,
                        principalTable: "JobSalaryTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayJobs_JobShiftTypes_JobShiftTypeId",
                        column: x => x.JobShiftTypeId,
                        principalTable: "JobShiftTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StayJobs_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StayPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StayListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhotoUrl = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    PhotoOrder = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayPhotos_StayListings_StayListingId",
                        column: x => x.StayListingId,
                        principalTable: "StayListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StayRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StayListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomIndex = table.Column<short>(type: "smallint", nullable: false),
                    PricePerNight = table.Column<int>(type: "integer", nullable: false),
                    MaxGuests = table.Column<short>(type: "smallint", nullable: false),
                    RoomName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Meals = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayRooms", x => x.Id);
                    table.CheckConstraint("ck_stayrooms_max_guests", "\"MaxGuests\" >= 1 AND \"MaxGuests\" <= 4");
                    table.CheckConstraint("ck_stayrooms_price_range", "\"PricePerNight\" >= 100 AND \"PricePerNight\" <= 9900000");
                    table.CheckConstraint("ck_stayrooms_room_name", "\"RoomName\" IN ('Standard', 'Deluxe', 'Premium', 'Suite', 'Family Room', 'Executive', 'Dormitory')");
                    table.ForeignKey(
                        name: "FK_StayRooms_StayListings_StayListingId",
                        column: x => x.StayListingId,
                        principalTable: "StayListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "Description", "IsActive", "Key", "Name", "SortOrder" },
                values: new object[] { new Guid("10000000-0000-0000-0000-000000000004"), "List your stay", true, "STAY_OWNER", "Stay Owner", 4 });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionTemplates_StayTypeId",
                table: "QuestionTemplates",
                column: "StayTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDepartments_Name",
                table: "JobDepartments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRoles_JobDepartmentId",
                table: "JobRoles",
                column: "JobDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRoles_JobDepartmentId_Name",
                table: "JobRoles",
                columns: new[] { "JobDepartmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobSalaryTypes_Name",
                table: "JobSalaryTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobShiftTypes_Name",
                table: "JobShiftTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTypes_Name",
                table: "JobTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_JobDepartmentId",
                table: "StayJobs",
                column: "JobDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_JobRoleId",
                table: "StayJobs",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_JobSalaryTypeId",
                table: "StayJobs",
                column: "JobSalaryTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_JobShiftTypeId",
                table: "StayJobs",
                column: "JobShiftTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_JobTypeId",
                table: "StayJobs",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "ix_stayjobs_status_created",
                table: "StayJobs",
                columns: new[] { "Status", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_StayListingId",
                table: "StayJobs",
                column: "StayListingId");

            migrationBuilder.CreateIndex(
                name: "IX_StayJobs_UserId_Status",
                table: "StayJobs",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_browse_district_city_created",
                table: "StayListings",
                columns: new[] { "DistrictId", "CityId", "CreatedAt" },
                descending: new[] { false, false, true },
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_browse_district_created",
                table: "StayListings",
                columns: new[] { "DistrictId", "CreatedAt" },
                descending: new[] { false, true },
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_browse_district_price",
                table: "StayListings",
                columns: new[] { "DistrictId", "MinPricePerNight" },
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_CityId",
                table: "StayListings",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_DistrictId",
                table: "StayListings",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_location_gist",
                table: "StayListings",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_ReviewedByAdminId",
                table: "StayListings",
                column: "ReviewedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_Slug",
                table: "StayListings",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_Status_SubmittedAt",
                table: "StayListings",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_StayTypeId",
                table: "StayListings",
                column: "StayTypeId");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_user_live",
                table: "StayListings",
                column: "UserId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_UserId_IsDeleted",
                table: "StayListings",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StayPhotos_StayListingId",
                table: "StayPhotos",
                column: "StayListingId");

            migrationBuilder.CreateIndex(
                name: "IX_StayRooms_StayListingId_RoomIndex",
                table: "StayRooms",
                columns: new[] { "StayListingId", "RoomIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayTypes_Name",
                table: "StayTypes",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_QuestionTemplates_StayTypes_StayTypeId",
                table: "QuestionTemplates",
                column: "StayTypeId",
                principalTable: "StayTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestionTemplates_StayTypes_StayTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DropTable(
                name: "StayJobs");

            migrationBuilder.DropTable(
                name: "StayPhotos");

            migrationBuilder.DropTable(
                name: "StayRooms");

            migrationBuilder.DropTable(
                name: "JobRoles");

            migrationBuilder.DropTable(
                name: "JobSalaryTypes");

            migrationBuilder.DropTable(
                name: "JobShiftTypes");

            migrationBuilder.DropTable(
                name: "JobTypes");

            migrationBuilder.DropTable(
                name: "StayListings");

            migrationBuilder.DropTable(
                name: "JobDepartments");

            migrationBuilder.DropTable(
                name: "StayTypes");

            migrationBuilder.DropIndex(
                name: "IX_QuestionTemplates_StayTypeId",
                table: "QuestionTemplates");

            migrationBuilder.DeleteData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"));

            migrationBuilder.DropColumn(
                name: "StayTypeId",
                table: "QuestionTemplates");
        }
    }
}

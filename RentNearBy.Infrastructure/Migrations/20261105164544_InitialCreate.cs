using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "Admins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Admins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    RenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingType = table.Column<string>(type: "text", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "Active"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessagePreview = table.Column<string>(type: "text", nullable: true),
                    UnreadCountForRenter = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UnreadCountForOwner = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeletedAccountRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OriginalUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    AccountCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DrivingLicenceNumberEnc = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    StayRegistrationTypeName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    StayRegistrationOtherName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StayRegistrationNumber = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    StayGstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedAccountRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Districts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StateName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    BusinessOnboardingEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Boundary = table.Column<Geometry>(type: "geometry(Geometry, 4326)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Districts", x => x.Id);
                });

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
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Razorpay"),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RazorpayOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RazorpayPaymentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlayPurchaseToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PlayNotificationType = table.Column<int>(type: "integer", nullable: true),
                    PlayOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RawBody = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    FeatureKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tagline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TrialDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    PlayProductId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                    table.CheckConstraint("ck_plans_trial_days", "\"TrialDays\" BETWEEN 0 AND 30");
                });

            migrationBuilder.CreateTable(
                name: "ReportReasons",
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
                    table.PrimaryKey("PK_ReportReasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StayRegistrationTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    RequiresOtherName = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayRegistrationTypes", x => x.Id);
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
                name: "UserBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminDeviceTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    IsValid = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminDeviceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminDeviceTokens_Admins_AdminId",
                        column: x => x.AdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdminSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LoggedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminSessions_Admins_AdminId",
                        column: x => x.AdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppFeatureFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    FeatureKey = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FreeDurationDays = table.Column<int>(type: "integer", nullable: true),
                    FreeModeListingLimit = table.Column<int>(type: "integer", nullable: true),
                    PaymentProvider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Play"),
                    UpdatedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppFeatureFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppFeatureFlags_Admins_UpdatedByAdminId",
                        column: x => x.UpdatedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AppTabs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TabKey = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppTabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppTabs_Admins_UpdatedByAdminId",
                        column: x => x.UpdatedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

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

            migrationBuilder.CreateTable(
                name: "Coupons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: false),
                    MaxDiscountInr = table.Column<int>(type: "integer", nullable: true),
                    MinPlanPriceInr = table.Column<int>(type: "integer", nullable: true),
                    FeatureKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AllowedPlanIds = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    NewUsersOnly = table.Column<bool>(type: "boolean", nullable: false),
                    FirstPurchaseOnly = table.Column<bool>(type: "boolean", nullable: false),
                    PerUserLimit = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    MaxTotalRedemptions = table.Column<int>(type: "integer", nullable: true),
                    RedemptionCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AutoApply = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CampaignLabel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Coupons", x => x.Id);
                    table.CheckConstraint("ck_coupons_value", "(\"Kind\" = 'Percent' AND \"Value\" BETWEEN 1 AND 100) OR (\"Kind\" <> 'Percent' AND \"Value\" >= 0)");
                    table.ForeignKey(
                        name: "FK_Coupons_Admins_CreatedByAdminId",
                        column: x => x.CreatedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    RespondsToMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Messages_Messages_RespondsToMessageId",
                        column: x => x.RespondsToMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    CityKey = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    NameLower = table.Column<string>(type: "text", nullable: true, computedColumnSql: "lower(\"Name\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cities_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DistrictBanners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    ImageFilePath = table.Column<string>(type: "text", nullable: false),
                    ContactNumber = table.Column<string>(type: "text", nullable: true),
                    RedirectUrl = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistrictBanners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DistrictBanners_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "PlanFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanFeatures", x => x.Id);
                    table.CheckConstraint("ck_planfeatures_value", "\"Value\" >= 0");
                    table.ForeignKey(
                        name: "FK_PlanFeatures_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleMonths = table.Column<short>(type: "smallint", nullable: false),
                    PriceInr = table.Column<int>(type: "integer", nullable: false),
                    RazorpayPlanId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlayBasePlanId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PlayTrialOfferId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanPrices", x => x.Id);
                    table.CheckConstraint("ck_planprices_cycle", "\"CycleMonths\" IN (1, 3, 12)");
                    table.CheckConstraint("ck_planprices_price", "\"PriceInr\" >= 1");
                    table.CheckConstraint("ck_planprices_trial_offer", "\"PlayTrialOfferId\" IS NULL OR \"PlayBasePlanId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_PlanPrices_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsPhoneVerified = table.Column<bool>(type: "boolean", nullable: false),
                    HasUsedPhoneChange = table.Column<bool>(type: "boolean", nullable: false),
                    RoleCorrectionUsed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsContactVisible = table.Column<bool>(type: "boolean", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("10000000-0000-0000-0000-000000000001")),
                    RoleConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlayAccountKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    NotificationsReadUpTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuestionTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Key = table.Column<string>(type: "text", nullable: false),
                    ListingType = table.Column<string>(type: "text", nullable: false),
                    StayTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuestionText = table.Column<string>(type: "text", nullable: false),
                    AnswerOptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionTemplates_StayTypes_StayTypeId",
                        column: x => x.StayTypeId,
                        principalTable: "StayTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "BannerDismissals",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BannerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannerDismissals", x => new { x.UserId, x.BannerId });
                    table.ForeignKey(
                        name: "FK_BannerDismissals_DistrictBanners_BannerId",
                        column: x => x.BannerId,
                        principalTable: "DistrictBanners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BannerDismissals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    IsValid = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ActionRoute = table.Column<string>(type: "text", nullable: true),
                    ActionArgumentsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationEvents_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationLogs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaySubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FeatureKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanPriceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ProductId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PurchasedProductId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DetachedReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    BasePlanId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    OfferId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LinkedPurchaseToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ExpiredPurchaseToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MappingStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AutoRenewEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiryTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatestOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AcknowledgementState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RegionCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsTestPurchase = table.Column<bool>(type: "boolean", nullable: false),
                    ObfuscatedAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PausedAutoResumeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeferredProductId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RecurringPriceMicros = table.Column<long>(type: "bigint", nullable: true),
                    RecurringPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    PriceChangeState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PriceChangeMode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastRtdnType = table.Column<int>(type: "integer", nullable: true),
                    LastEventTimeMillis = table.Column<long>(type: "bigint", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    PendingServerAction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PendingActionReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PendingActionAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    NextActionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnonymisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaySubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaySubscriptions_PlanPrices_PlanPriceId",
                        column: x => x.PlanPriceId,
                        principalTable: "PlanPrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlaySubscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaySubscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RoleChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "PENDING"),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleChangeRequests_Admins_ReviewedByAdminId",
                        column: x => x.ReviewedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RoleChangeRequests_Roles_FromRoleId",
                        column: x => x.FromRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleChangeRequests_Roles_ToRoleId",
                        column: x => x.ToRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleChangeRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LoggedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    RegistrationTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationTypeName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RegistrationOtherName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RegistrationNumber = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
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
                    EverApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AcceptingGuests = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RejectedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinPricePerNight = table.Column<int>(type: "integer", nullable: true),
                    Slug = table.Column<string>(type: "text", nullable: true),
                    PriorityRank = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    IsFeatured = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpiId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    WhatsAppNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    TripEnquiriesReceived = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TripEnquiriesAnsweredIn24h = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
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
                        name: "FK_StayListings_StayRegistrationTypes_RegistrationTypeId",
                        column: x => x.RegistrationTypeId,
                        principalTable: "StayRegistrationTypes",
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
                    DrivingLicenceNumberEnc = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    EverApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RejectedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinFare = table.Column<int>(type: "integer", nullable: true),
                    LocalDayRate = table.Column<int>(type: "integer", nullable: true),
                    LocalDayKm = table.Column<int>(type: "integer", nullable: true),
                    Slug = table.Column<string>(type: "text", nullable: true),
                    PriorityRank = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    IsFeatured = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TripLeadsReceived = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TripLeadsAnsweredInApp = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
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
                    table.CheckConstraint("ck_taxilistings_local_day_km_range", "\"LocalDayKm\" IS NULL OR (\"LocalDayKm\" >= 0 AND \"LocalDayKm\" <= 1000)");
                    table.CheckConstraint("ck_taxilistings_local_day_rate_range", "\"LocalDayRate\" IS NULL OR (\"LocalDayRate\" >= 100 AND \"LocalDayRate\" <= 9900000)");
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
                name: "Trips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityKey = table.Column<long>(type: "bigint", nullable: false),
                    CityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CityLatitude = table.Column<double>(type: "double precision", nullable: false),
                    CityLongitude = table.Column<double>(type: "double precision", nullable: false),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    DayCount = table.Column<int>(type: "integer", nullable: false),
                    SpotCount = table.Column<int>(type: "integer", nullable: false),
                    ClientTripId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.CheckConstraint("ck_trips_citykey_positive", "\"CityKey\" > 0");
                    table.ForeignKey(
                        name: "FK_Trips_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trips_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReads",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReads", x => new { x.UserId, x.NotificationId });
                    table.ForeignKey(
                        name: "FK_NotificationReads_NotificationEvents_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "NotificationEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationReads_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalPaidInr = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotalDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PlaySubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlayAutoRenewEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    PlayState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PlayTrialEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CarryOverDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PlayScheduledPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPlans", x => x.Id);
                    table.CheckConstraint("ck_userplans_carryover", "\"CarryOverDays\" >= 0");
                    table.CheckConstraint("ck_userplans_window", "\"EndsAt\" > \"StartsAt\"");
                    table.ForeignKey(
                        name: "FK_UserPlans_Admins_GrantedByAdminId",
                        column: x => x.GrantedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserPlans_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserPlans_Plans_PlayScheduledPlanId",
                        column: x => x.PlayScheduledPlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserPlans_PlaySubscriptions_PlaySubscriptionId",
                        column: x => x.PlaySubscriptionId,
                        principalTable: "PlaySubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserPlans_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    Floor = table.Column<short>(type: "smallint", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "SavedJourneys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FromLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    FromLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ToLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    TravelDate = table.Column<DateOnly>(type: "date", nullable: false),
                    People = table.Column<short>(type: "smallint", nullable: false),
                    PlanKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cars = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ListedTotal = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastRefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedJourneys", x => x.Id);
                    table.CheckConstraint("ck_savedjourneys_cars", "\"Cars\" BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_savedjourneys_people", "\"People\" BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_savedjourneys_status", "\"Status\" IN ('Ready','DriverUnavailable','NotAsked','Completed')");
                    table.ForeignKey(
                        name: "FK_SavedJourneys_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SavedJourneys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripProblemLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripEnquiryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Day = table.Column<short>(type: "smallint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripProblemLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripProblemLogs_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripPushLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    TripEnquiryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripPushLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripPushLogs_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FeatureKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanNameSnapshot = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CycleMonths = table.Column<short>(type: "smallint", nullable: true),
                    GrantedDays = table.Column<int>(type: "integer", nullable: false),
                    Intent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "INR"),
                    ListPriceInr = table.Column<int>(type: "integer", nullable: false),
                    DiscountInr = table.Column<int>(type: "integer", nullable: false),
                    CreditInr = table.Column<int>(type: "integer", nullable: false),
                    PayableInr = table.Column<int>(type: "integer", nullable: false),
                    CouponId = table.Column<Guid>(type: "uuid", nullable: true),
                    CouponCodeSnapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RazorpayOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RazorpayPaymentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CapturedAmountPaise = table.Column<long>(type: "bigint", nullable: true),
                    RazorpayRefundId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GrantBlockedReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FailedPaymentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GrantedUserPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientIdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnonymisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlaySubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlayOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlayProductId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PlayBasePlanId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PlayOfferId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PlayPurchaseSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PlayChargebackReviewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_Admins_GrantedByAdminId",
                        column: x => x.GrantedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_Coupons_CouponId",
                        column: x => x.CouponId,
                        principalTable: "Coupons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_PlaySubscriptions_PlaySubscriptionId",
                        column: x => x.PlaySubscriptionId,
                        principalTable: "PlaySubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_UserPlans_GrantedUserPlanId",
                        column: x => x.GrantedUserPlanId,
                        principalTable: "UserPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlanPurchases_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaxiRouteGeometries",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    GeometryJson = table.Column<string>(type: "jsonb", nullable: true),
                    DistanceKm = table.Column<decimal>(type: "numeric(7,1)", nullable: true),
                    Minutes = table.Column<int>(type: "integer", nullable: true),
                    Attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiRouteGeometries", x => x.RouteId);
                    table.CheckConstraint("ck_taxiroutegeometries_status", "\"Status\" IN ('Pending','Ok','NoRoute','Uncovered','Suspect','Failed')");
                    table.ForeignKey(
                        name: "FK_TaxiRouteGeometries_TaxiRoutes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "TaxiRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CouponRedemptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CouponId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiscountInr = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponRedemptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponRedemptions_Coupons_CouponId",
                        column: x => x.CouponId,
                        principalTable: "Coupons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CouponRedemptions_PlanPurchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "PlanPurchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CouponRedemptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ListingReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingType = table.Column<string>(type: "text", nullable: false),
                    ReporterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterName = table.Column<string>(type: "text", nullable: false),
                    ReporterMobile = table.Column<string>(type: "text", nullable: false),
                    ReportedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportedName = table.Column<string>(type: "text", nullable: false),
                    ReportedMobile = table.Column<string>(type: "text", nullable: false),
                    ReasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "Pending"),
                    ResolutionAction = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    TripEnquiryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingReports_Admins_ResolvedByAdminId",
                        column: x => x.ResolvedByAdminId,
                        principalTable: "Admins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ListingReports_ReportReasons_ReasonId",
                        column: x => x.ReasonId,
                        principalTable: "ReportReasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedJourneyLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    JourneyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegIndex = table.Column<short>(type: "smallint", nullable: false),
                    LegKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FromName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FromLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    FromLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ToLatitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    ToLongitude = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    MeetingPlace = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DriverTaxiId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverRouteId = table.Column<Guid>(type: "uuid", nullable: true),
                    ListedFare = table.Column<int>(type: "integer", nullable: true),
                    Partial = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Backup1TaxiId = table.Column<Guid>(type: "uuid", nullable: true),
                    Backup2TaxiId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastEnquiryId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastKnownStatus = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "Unknown"),
                    LastQuotedFare = table.Column<int>(type: "integer", nullable: true),
                    LastKnownAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DriverAvailable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedJourneyLegs", x => x.Id);
                    table.CheckConstraint("ck_savedjourneylegs_index", "\"LegIndex\" BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_savedjourneylegs_status", "\"LastKnownStatus\" IN ('Free','NotFree','NoAnswer','Unknown')");
                    table.ForeignKey(
                        name: "FK_SavedJourneyLegs_SavedJourneys_JourneyId",
                        column: x => x.JourneyId,
                        principalTable: "SavedJourneys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripEnquiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    TravellerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ListingLatitude = table.Column<double>(type: "double precision", nullable: true),
                    ListingLongitude = table.Column<double>(type: "double precision", nullable: true),
                    Day = table.Column<short>(type: "smallint", nullable: true),
                    DaysJson = table.Column<string>(type: "jsonb", nullable: true),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOut = table.Column<DateOnly>(type: "date", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FailReason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsUrgent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AcceptedOfferId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgreedTotal = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedByTravellerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByOwnerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PhoneBookingJson = table.Column<string>(type: "jsonb", nullable: true),
                    HostConfirmTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HostConfirmTokenIssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostConfirmTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostConfirmLinkCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    HostConfirmAnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostConfirmAnswer = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    HostConfirmAnsweredVia = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    HostConfirmAnsweredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DriverFareEditCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    AdvanceStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "None"),
                    AdvanceAmount = table.Column<int>(type: "integer", nullable: true),
                    HoldUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdvanceAskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProofSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdvanceReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdvanceNotYetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHostReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HostReminderCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    ListedRateSnapshot = table.Column<int>(type: "integer", nullable: true),
                    DriverFare = table.Column<int>(type: "integer", nullable: true),
                    AgreedFare = table.Column<int>(type: "integer", nullable: true),
                    DriverNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DriverRepliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BookedByTravellerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TollExtra = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PickupTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    PickupPlace = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripEnquiries", x => x.Id);
                    table.CheckConstraint("ck_tripenquiries_advance_status", "\"AdvanceStatus\" IN ('None','Pending','ProofSent','Received','HoldExpired')");
                    table.CheckConstraint("ck_tripenquiries_amounts", "(\"AgreedTotal\" IS NULL OR \"AgreedTotal\" >= 0) AND (\"AdvanceAmount\" IS NULL OR \"AdvanceAmount\" >= 0) AND (\"DriverFare\" IS NULL OR \"DriverFare\" >= 0) AND (\"AgreedFare\" IS NULL OR \"AgreedFare\" >= 0) AND (\"ListedRateSnapshot\" IS NULL OR \"ListedRateSnapshot\" >= 0)");
                    table.CheckConstraint("ck_tripenquiries_kind", "\"Kind\" IN ('Stay','Taxi')");
                    table.CheckConstraint("ck_tripenquiries_status", "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller','NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Failed','Closed')");
                    table.ForeignKey(
                        name: "FK_TripEnquiries_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripEnquiries_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripEnquiries_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripEnquiries_Users_TravellerUserId",
                        column: x => x.TravellerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TripOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripEnquiryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOut = table.Column<DateOnly>(type: "date", nullable: true),
                    LinesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Nights = table.Column<short>(type: "smallint", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Inclusions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NeedsEchoedJson = table.Column<string>(type: "jsonb", nullable: true),
                    AdvanceAmount = table.Column<int>(type: "integer", nullable: true),
                    BalanceNote = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CancellationText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    GstInvoice = table.Column<bool>(type: "boolean", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Adults = table.Column<short>(type: "smallint", nullable: true),
                    Kids = table.Column<short>(type: "smallint", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripOffers_TripEnquiries_TripEnquiryId",
                        column: x => x.TripEnquiryId,
                        principalTable: "TripEnquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "Description", "IsActive", "Key", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "Plan trips, find stays and taxis", true, "SEEKER", "Seeker", 1 },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "List your stay", true, "STAY_OWNER", "Stay Owner", 4 },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "List your taxi", true, "TAXI_DRIVER", "Taxi Driver", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_AdminId",
                table: "AdminDeviceTokens",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_AdminId_IsValid",
                table: "AdminDeviceTokens",
                columns: new[] { "AdminId", "IsValid" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_IsValid",
                table: "AdminDeviceTokens",
                column: "IsValid");

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_Token",
                table: "AdminDeviceTokens",
                column: "Token");

            migrationBuilder.CreateIndex(
                name: "IX_Admins_PhoneNumber",
                table: "Admins",
                column: "PhoneNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_AdminId",
                table: "AdminSessions",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_ExpiresAt",
                table: "AdminSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AppFeatureFlags_FeatureKey",
                table: "AppFeatureFlags",
                column: "FeatureKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppFeatureFlags_UpdatedByAdminId",
                table: "AppFeatureFlags",
                column: "UpdatedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AppTabs_TabKey",
                table: "AppTabs",
                column: "TabKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppTabs_UpdatedByAdminId",
                table: "AppTabs",
                column: "UpdatedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AppVersionConfigs_Platform",
                table: "AppVersionConfigs",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppVersionConfigs_UpdatedByAdminId",
                table: "AppVersionConfigs",
                column: "UpdatedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_BannerDismissals_BannerId",
                table: "BannerDismissals",
                column: "BannerId");

            migrationBuilder.CreateIndex(
                name: "ix_cities_citykey_unique",
                table: "Cities",
                column: "CityKey",
                unique: true,
                filter: "\"CityKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cities_district_namelower",
                table: "Cities",
                columns: new[] { "DistrictId", "NameLower" });

            migrationBuilder.CreateIndex(
                name: "ix_cities_district_namelower_unique",
                table: "Cities",
                columns: new[] { "DistrictId", "NameLower" },
                unique: true,
                filter: "\"CityKey\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cities_DistrictId",
                table: "Cities",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_OwnerId_LastMessageAt_Id",
                table: "Conversations",
                columns: new[] { "OwnerId", "LastMessageAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_renter_owner_listing",
                table: "Conversations",
                columns: new[] { "RenterId", "OwnerId", "ListingType", "ListingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_RenterId_LastMessageAt_Id",
                table: "Conversations",
                columns: new[] { "RenterId", "LastMessageAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_couponredemptions_coupon_user_active",
                table: "CouponRedemptions",
                columns: new[] { "CouponId", "UserId" },
                unique: true,
                filter: "\"Status\" IN ('Reserved', 'Consumed')");

            migrationBuilder.CreateIndex(
                name: "IX_CouponRedemptions_PurchaseId",
                table: "CouponRedemptions",
                column: "PurchaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CouponRedemptions_UserId",
                table: "CouponRedemptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ix_coupons_autoapply_per_feature",
                table: "Coupons",
                column: "FeatureKey",
                unique: true,
                filter: "\"AutoApply\" AND \"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_Code",
                table: "Coupons",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_CreatedByAdminId",
                table: "Coupons",
                column: "CreatedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_Status",
                table: "Coupons",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeletedAccountRecords_DeletedAt",
                table: "DeletedAccountRecords",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeletedAccountRecords_PhoneNumber",
                table: "DeletedAccountRecords",
                column: "PhoneNumber");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_Token",
                table: "DeviceTokens",
                column: "Token");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId",
                table: "DeviceTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId_IsValid",
                table: "DeviceTokens",
                columns: new[] { "UserId", "IsValid" });

            migrationBuilder.CreateIndex(
                name: "IX_DistrictBanners_DistrictId",
                table: "DistrictBanners",
                column: "DistrictId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_districts_boundary_active_gist",
                table: "Districts",
                column: "Boundary",
                filter: "\"IsActive\" = true")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_Districts_IsActive",
                table: "Districts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Districts_StateName",
                table: "Districts",
                column: "StateName");

            migrationBuilder.CreateIndex(
                name: "IX_Districts_StateName_Name",
                table: "Districts",
                columns: new[] { "StateName", "Name" },
                unique: true);

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
                name: "IX_ListingReports_ListingId_ListingType_Status",
                table: "ListingReports",
                columns: new[] { "ListingId", "ListingType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_ReasonId",
                table: "ListingReports",
                column: "ReasonId");

            migrationBuilder.CreateIndex(
                name: "ix_listingreports_reporter_listing_pending",
                table: "ListingReports",
                columns: new[] { "ReporterUserId", "ListingId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_ResolvedByAdminId",
                table: "ListingReports",
                column: "ResolvedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_Status",
                table: "ListingReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_Status_CreatedAt_Id",
                table: "ListingReports",
                columns: new[] { "Status", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_TripEnquiryId",
                table: "ListingReports",
                column: "TripEnquiryId");

            migrationBuilder.CreateIndex(
                name: "ix_messages_client_message_id_unique",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ClientMessageId" },
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_CreatedAt",
                table: "Messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SenderId_ReadAt",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_responds_to_unique",
                table: "Messages",
                column: "RespondsToMessageId",
                unique: true,
                filter: "\"RespondsToMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notificationevents_created",
                table: "NotificationEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvents_TargetUserId_CreatedAt_Id",
                table: "NotificationEvents",
                columns: new[] { "TargetUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_UserId",
                table: "NotificationLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_UserId_Type_SentAt",
                table: "NotificationLogs",
                columns: new[] { "UserId", "Type", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReads_NotificationId",
                table: "NotificationReads",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_EventId",
                table: "PaymentEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_paymentevents_play_order",
                table: "PaymentEvents",
                column: "PlayOrderId");

            migrationBuilder.CreateIndex(
                name: "ix_paymentevents_play_token",
                table: "PaymentEvents",
                column: "PlayPurchaseToken");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_RazorpayOrderId",
                table: "PaymentEvents",
                column: "RazorpayOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_ReceivedAt",
                table: "PaymentEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlanFeatures_PlanId_Key",
                table: "PlanFeatures",
                columns: new[] { "PlanId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanPrices_PlanId_CycleMonths",
                table: "PlanPrices",
                columns: new[] { "PlanId", "CycleMonths" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_planprices_play_baseplan",
                table: "PlanPrices",
                columns: new[] { "PlanId", "PlayBasePlanId" },
                unique: true,
                filter: "\"PlayBasePlanId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlanPurchases_CouponId",
                table: "PlanPurchases",
                column: "CouponId");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_created_keyset",
                table: "PlanPurchases",
                columns: new[] { "CreatedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_PlanPurchases_GrantedByAdminId",
                table: "PlanPurchases",
                column: "GrantedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanPurchases_GrantedUserPlanId",
                table: "PlanPurchases",
                column: "GrantedUserPlanId");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_idempotency",
                table: "PlanPurchases",
                column: "ClientIdempotencyKey",
                unique: true,
                filter: "\"ClientIdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_one_open",
                table: "PlanPurchases",
                columns: new[] { "UserId", "FeatureKey" },
                unique: true,
                filter: "\"Status\" IN ('Created', 'Pending')");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_order",
                table: "PlanPurchases",
                column: "RazorpayOrderId",
                unique: true,
                filter: "\"RazorpayOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_payment",
                table: "PlanPurchases",
                column: "RazorpayPaymentId",
                unique: true,
                filter: "\"RazorpayPaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlanPurchases_PlanId",
                table: "PlanPurchases",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_play_order",
                table: "PlanPurchases",
                column: "PlayOrderId",
                unique: true,
                filter: "\"PlayOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_play_subscription",
                table: "PlanPurchases",
                column: "PlaySubscriptionId");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_refund",
                table: "PlanPurchases",
                column: "RazorpayRefundId",
                unique: true,
                filter: "\"RazorpayRefundId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_status_created",
                table: "PlanPurchases",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_planpurchases_user_created",
                table: "PlanPurchases",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_plans_default_per_feature",
                table: "Plans",
                column: "FeatureKey",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_FeatureKey_Code",
                table: "Plans",
                columns: new[] { "FeatureKey", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plans_play_product",
                table: "Plans",
                column: "PlayProductId",
                unique: true,
                filter: "\"PlayProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_last_synced",
                table: "PlaySubscriptions",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_linked_token",
                table: "PlaySubscriptions",
                column: "LinkedPurchaseToken");

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_mapping",
                table: "PlaySubscriptions",
                column: "MappingStatus");

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_pending_action",
                table: "PlaySubscriptions",
                column: "PendingServerAction",
                filter: "\"PendingServerAction\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlaySubscriptions_PlanId",
                table: "PlaySubscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaySubscriptions_PlanPriceId",
                table: "PlaySubscriptions",
                column: "PlanPriceId");

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_state_expiry",
                table: "PlaySubscriptions",
                columns: new[] { "State", "ExpiryTime" });

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_token",
                table: "PlaySubscriptions",
                column: "PurchaseToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playsubscriptions_user_feature",
                table: "PlaySubscriptions",
                columns: new[] { "UserId", "FeatureKey" });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionTemplates_Key",
                table: "QuestionTemplates",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionTemplates_StayTypeId",
                table: "QuestionTemplates",
                column: "StayTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportReasons_Name",
                table: "ReportReasons",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_FromRoleId",
                table: "RoleChangeRequests",
                column: "FromRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_ReviewedByAdminId",
                table: "RoleChangeRequests",
                column: "ReviewedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_Status_RequestedAt",
                table: "RoleChangeRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_Status_RequestedAt_Id",
                table: "RoleChangeRequests",
                columns: new[] { "Status", "RequestedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_ToRoleId",
                table: "RoleChangeRequests",
                column: "ToRoleId");

            migrationBuilder.CreateIndex(
                name: "ix_rolechangerequests_user_pending",
                table: "RoleChangeRequests",
                column: "UserId",
                unique: true,
                filter: "\"Status\" = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_RoleChangeRequests_UserId_RequestedAt",
                table: "RoleChangeRequests",
                columns: new[] { "UserId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Key",
                table: "Roles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_savedjourneylegs_driver",
                table: "SavedJourneyLegs",
                column: "DriverTaxiId",
                filter: "\"DriverAvailable\" = true");

            migrationBuilder.CreateIndex(
                name: "ix_savedjourneylegs_journey_index",
                table: "SavedJourneyLegs",
                columns: new[] { "JourneyId", "LegIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedJourneyLegs_LastEnquiryId",
                table: "SavedJourneyLegs",
                column: "LastEnquiryId");

            migrationBuilder.CreateIndex(
                name: "ix_savedjourneys_travel",
                table: "SavedJourneys",
                column: "TravelDate",
                filter: "\"IsDeleted\" = false AND \"Status\" <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_SavedJourneys_TripId",
                table: "SavedJourneys",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ix_savedjourneys_user",
                table: "SavedJourneys",
                columns: new[] { "UserId", "UpdatedAt" },
                descending: new[] { false, true },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExpiresAt",
                table: "Sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

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
                columns: new[] { "DistrictId", "CityId", "PriorityRank", "CreatedAt" },
                descending: new[] { false, false, true, true },
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_browse_district_created",
                table: "StayListings",
                columns: new[] { "DistrictId", "PriorityRank", "CreatedAt" },
                descending: new[] { false, true, true },
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_staylistings_browse_district_price",
                table: "StayListings",
                columns: new[] { "DistrictId", "PriorityRank", "MinPricePerNight" },
                descending: new[] { false, true, false },
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
                name: "ix_staylistings_recent_active",
                table: "StayListings",
                columns: new[] { "PriorityRank", "CreatedAt" },
                descending: new bool[0],
                filter: "\"Status\" = 'Approved' AND \"AcceptingGuests\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_RegistrationTypeId",
                table: "StayListings",
                column: "RegistrationTypeId");

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
                name: "ix_stayregistrationtypes_code",
                table: "StayRegistrationTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stayregistrationtypes_name",
                table: "StayRegistrationTypes",
                column: "Name",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_browse_district_city_fare",
                table: "TaxiListings",
                columns: new[] { "DistrictId", "CityId", "PriorityRank", "MinFare" },
                descending: new[] { false, false, true, false },
                filter: "\"Status\" = 'Approved' AND \"IsAvailable\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_taxilistings_browse_district_fare",
                table: "TaxiListings",
                columns: new[] { "DistrictId", "PriorityRank", "MinFare" },
                descending: new[] { false, true, false },
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
                name: "ix_taxilistings_trip_browse",
                table: "TaxiListings",
                columns: new[] { "DistrictId", "PriorityRank", "LocalDayRate" },
                descending: new[] { false, true, false },
                filter: "\"Status\" = 'Approved' AND \"IsAvailable\" = true AND \"IsDeleted\" = false AND \"LocalDayRate\" IS NOT NULL");

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
                name: "ix_taxiroutegeometries_status",
                table: "TaxiRouteGeometries",
                columns: new[] { "Status", "UpdatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_TripEnquiries_AcceptedOfferId",
                table: "TripEnquiries",
                column: "AcceptedOfferId");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_asked",
                table: "TripEnquiries",
                column: "CreatedAt",
                filter: "\"Status\" = 'Asked'");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_checkin_unconfirmed",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "\"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_conversation",
                table: "TripEnquiries",
                column: "ConversationId",
                filter: "\"ConversationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_hold",
                table: "TripEnquiries",
                column: "HoldUntil",
                filter: "\"AdvanceStatus\" IN ('Pending','ProofSent')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_host_token",
                table: "TripEnquiries",
                column: "HostConfirmTokenHash",
                unique: true,
                filter: "\"HostConfirmTokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_hostnc",
                table: "TripEnquiries",
                column: "HostConfirmTokenIssuedAt",
                filter: "\"Status\" = 'ConfirmedByTraveller' AND \"HostConfirmAnsweredAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_notme",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "HostConfirmAnswer", "HostConfirmAnsweredAt" });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_open_taxi_unique",
                table: "TripEnquiries",
                columns: new[] { "TripId", "Kind", "ListingId" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_open_unique",
                table: "TripEnquiries",
                columns: new[] { "TripId", "Kind", "ListingId", "Day" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_orphan",
                table: "TripEnquiries",
                column: "UpdatedAt",
                filter: "\"TripId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_owner_status",
                table: "TripEnquiries",
                columns: new[] { "OwnerUserId", "Status", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_proof",
                table: "TripEnquiries",
                column: "ProofSentAt",
                filter: "\"AdvanceStatus\" = 'ProofSent'");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_traveller",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "Kind", "ListingId" },
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_trip",
                table: "TripEnquiries",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "NOT \"IsUrgent\" AND \"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripoffers_enquiry",
                table: "TripOffers",
                columns: new[] { "TripEnquiryId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tripoffers_valid",
                table: "TripOffers",
                column: "ValidUntil",
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_tripproblemlogs_trip",
                table: "TripProblemLogs",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ix_trippushlogs_key",
                table: "TripPushLogs",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trippushlogs_sent",
                table: "TripPushLogs",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_TripPushLogs_TripId",
                table: "TripPushLogs",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_DistrictId",
                table: "Trips",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "ix_trips_enddate",
                table: "Trips",
                column: "EndDate",
                filter: "\"EndDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trips_owner_client",
                table: "Trips",
                columns: new[] { "OwnerUserId", "ClientTripId" },
                unique: true,
                filter: "\"ClientTripId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trips_owner_updated",
                table: "Trips",
                columns: new[] { "OwnerUserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_trips_startdate",
                table: "Trips",
                column: "StartDate",
                filter: "\"StartDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserBlocks_BlockerId_BlockedId",
                table: "UserBlocks",
                columns: new[] { "BlockerId", "BlockedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_userplans_active_per_feature",
                table: "UserPlans",
                columns: new[] { "UserId", "FeatureKey" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlans_GrantedByAdminId",
                table: "UserPlans",
                column: "GrantedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlans_PlanId",
                table: "UserPlans",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "ix_userplans_play_subscription",
                table: "UserPlans",
                column: "PlaySubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlans_PlayScheduledPlanId",
                table: "UserPlans",
                column: "PlayScheduledPlanId");

            migrationBuilder.CreateIndex(
                name: "ix_userplans_status_endsat",
                table: "UserPlans",
                columns: new[] { "Status", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "ix_userplans_trial_once",
                table: "UserPlans",
                columns: new[] { "UserId", "FeatureKey" },
                unique: true,
                filter: "\"Source\" = 'Trial'");

            migrationBuilder.CreateIndex(
                name: "ix_userplans_user_feature_created",
                table: "UserPlans",
                columns: new[] { "UserId", "FeatureKey", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedAt",
                table: "Users",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PhoneNumber",
                table: "Users",
                column: "PhoneNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_play_account_key",
                table: "Users",
                column: "PlayAccountKey",
                unique: true,
                filter: "\"PlayAccountKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_ListingReports_TripEnquiries_TripEnquiryId",
                table: "ListingReports",
                column: "TripEnquiryId",
                principalTable: "TripEnquiries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SavedJourneyLegs_TripEnquiries_LastEnquiryId",
                table: "SavedJourneyLegs",
                column: "LastEnquiryId",
                principalTable: "TripEnquiries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TripEnquiries_TripOffers_AcceptedOfferId",
                table: "TripEnquiries",
                column: "AcceptedOfferId",
                principalTable: "TripOffers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TripEnquiries_Users_OwnerUserId",
                table: "TripEnquiries");

            migrationBuilder.DropForeignKey(
                name: "FK_TripEnquiries_Users_TravellerUserId",
                table: "TripEnquiries");

            migrationBuilder.DropForeignKey(
                name: "FK_Trips_Users_OwnerUserId",
                table: "Trips");

            migrationBuilder.DropForeignKey(
                name: "FK_Trips_Districts_DistrictId",
                table: "Trips");

            migrationBuilder.DropForeignKey(
                name: "FK_TripOffers_TripEnquiries_TripEnquiryId",
                table: "TripOffers");

            migrationBuilder.DropTable(
                name: "AdminDeviceTokens");

            migrationBuilder.DropTable(
                name: "AdminSessions");

            migrationBuilder.DropTable(
                name: "AppFeatureFlags");

            migrationBuilder.DropTable(
                name: "AppTabs");

            migrationBuilder.DropTable(
                name: "AppVersionConfigs");

            migrationBuilder.DropTable(
                name: "BannerDismissals");

            migrationBuilder.DropTable(
                name: "CouponRedemptions");

            migrationBuilder.DropTable(
                name: "DeletedAccountRecords");

            migrationBuilder.DropTable(
                name: "DeviceTokens");

            migrationBuilder.DropTable(
                name: "ListingReports");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "NotificationReads");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PlanFeatures");

            migrationBuilder.DropTable(
                name: "QuestionTemplates");

            migrationBuilder.DropTable(
                name: "RoleChangeRequests");

            migrationBuilder.DropTable(
                name: "SavedJourneyLegs");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "StayJobs");

            migrationBuilder.DropTable(
                name: "StayPhotos");

            migrationBuilder.DropTable(
                name: "StayRooms");

            migrationBuilder.DropTable(
                name: "TaxiRouteGeometries");

            migrationBuilder.DropTable(
                name: "TripProblemLogs");

            migrationBuilder.DropTable(
                name: "TripPushLogs");

            migrationBuilder.DropTable(
                name: "UserBlocks");

            migrationBuilder.DropTable(
                name: "DistrictBanners");

            migrationBuilder.DropTable(
                name: "PlanPurchases");

            migrationBuilder.DropTable(
                name: "ReportReasons");

            migrationBuilder.DropTable(
                name: "NotificationEvents");

            migrationBuilder.DropTable(
                name: "SavedJourneys");

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
                name: "TaxiRoutes");

            migrationBuilder.DropTable(
                name: "Coupons");

            migrationBuilder.DropTable(
                name: "UserPlans");

            migrationBuilder.DropTable(
                name: "JobDepartments");

            migrationBuilder.DropTable(
                name: "StayRegistrationTypes");

            migrationBuilder.DropTable(
                name: "StayTypes");

            migrationBuilder.DropTable(
                name: "TaxiListings");

            migrationBuilder.DropTable(
                name: "PlaySubscriptions");

            migrationBuilder.DropTable(
                name: "Admins");

            migrationBuilder.DropTable(
                name: "Cities");

            migrationBuilder.DropTable(
                name: "TaxiVehicleTypes");

            migrationBuilder.DropTable(
                name: "PlanPrices");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Districts");

            migrationBuilder.DropTable(
                name: "TripEnquiries");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "TripOffers");

            migrationBuilder.DropTable(
                name: "Trips");
        }
    }
}

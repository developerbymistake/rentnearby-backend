using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "SEEKER"),
                    AccountCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Username = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
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
                name: "DriverDispatchStates",
                columns: table => new
                {
                    DriverListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityKey = table.Column<long>(type: "bigint", nullable: false),
                    Pass = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    SeenSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastOfferedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverDispatchStates", x => new { x.DriverListingId, x.CityKey });
                });

            migrationBuilder.CreateTable(
                name: "DueActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNo = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "Pending"),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DueActions", x => x.Id);
                    table.CheckConstraint("ck_dueactions_done", "(\"State\" IN ('Pending','Running')) = (\"DoneAt\" IS NULL)");
                    table.CheckConstraint("ck_dueactions_kind", "\"Kind\" IN ('OfferExpire', 'ConfirmExpire', 'RequestWaitExpire', 'RequestStaleExpire', 'BookingStart', 'BookingAutoComplete', 'ChooseReminderFirst', 'ChooseReminderSecond')");
                    table.CheckConstraint("ck_dueactions_state", "\"State\" IN ('Pending', 'Running', 'Done', 'Cancelled', 'Skipped', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "FestivalDates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    StateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FestivalDates", x => x.Id);
                    table.CheckConstraint("ck_festivaldates_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 29");
                    table.CheckConstraint("ck_festivaldates_name", "length(btrim(\"Name\")) >= 1");
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
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Exchange = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RoutingKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OwnerReminderRuns",
                columns: table => new
                {
                    RunDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ListingKind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sent = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerReminderRuns", x => new { x.RunDate, x.ListingKind });
                    table.CheckConstraint("ck_ownerreminderruns_listingkind", "\"ListingKind\" IN ('Stay', 'Taxi')");
                });

            migrationBuilder.CreateTable(
                name: "OwnerWeeklyStats",
                columns: table => new
                {
                    ListingKind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Shown = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RequestsNoRoute = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerWeeklyStats", x => new { x.ListingKind, x.ListingId, x.Day });
                    table.CheckConstraint("ck_ownerweeklystats_listingkind", "\"ListingKind\" IN ('Stay', 'Taxi')");
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
                name: "PopularRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    FromCityKey = table.Column<long>(type: "bigint", nullable: false),
                    ToCityKey = table.Column<long>(type: "bigint", nullable: false),
                    FromCityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ToCityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PopularRoutes", x => x.Id);
                    table.CheckConstraint("ck_popularroutes_pair", "\"FromCityKey\" <> \"ToCityKey\"");
                });

            migrationBuilder.CreateTable(
                name: "ReportReasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "Listing"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportReasons", x => x.Id);
                    table.CheckConstraint("ck_reportreasons_scope", "\"Scope\" IN ('Listing', 'User')");
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
                name: "TaxiBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravellerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CancelledBy = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    CancelChip = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    TripName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PickupCityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DriverName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    VehicleName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Seats = table.Column<short>(type: "smallint", nullable: false),
                    TravellerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BookedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionCountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiBookings", x => x.Id);
                    table.CheckConstraint("ck_taxibookings_dates", "\"DateTo\" >= \"DateFrom\"");
                    table.CheckConstraint("ck_taxibookings_status", "\"Status\" IN ('Booked', 'InProgress', 'Completed', 'Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "TaxiDispatchCities",
                columns: table => new
                {
                    CityKey = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastMinPass = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiDispatchCities", x => x.CityKey);
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
                name: "TripReminderRuns",
                columns: table => new
                {
                    RunDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Sent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripReminderRuns", x => x.RunDate);
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
                    table.CheckConstraint("ck_userblocks_not_self", "\"BlockerId\" <> \"BlockedId\"");
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
                    Reason = table.Column<string>(type: "text", nullable: true),
                    AnnounceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentEnabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Username = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsPhoneVerified = table.Column<bool>(type: "boolean", nullable: false),
                    HasUsedPhoneChange = table.Column<bool>(type: "boolean", nullable: false),
                    RoleCorrectionUsed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsContactVisible = table.Column<bool>(type: "boolean", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("10000000-0000-0000-0000-000000000001")),
                    RoleConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlayAccountKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BuddyRequestsHeldUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProfilePhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProfilePhotoPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ProfilePhotoUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedTripsCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CompletedBookingsCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    NotificationsReadUpTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("ck_users_completedbookings_nonneg", "\"CompletedBookingsCount\" >= 0");
                    table.CheckConstraint("ck_users_completedtrips_nonneg", "\"CompletedTripsCount\" >= 0");
                    table.CheckConstraint("ck_users_profilephoto_pair", "(\"ProfilePhotoUrl\" IS NULL) = (\"ProfilePhotoPath\" IS NULL)");
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
                name: "TaxiRatings",
                columns: table => new
                {
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stars = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiRatings", x => x.BookingId);
                    table.CheckConstraint("ck_taxiratings_stars", "\"Stars\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_TaxiRatings_TaxiBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "TaxiBookings",
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
                    table.CheckConstraint("ck_stayjobs_status", "\"Status\" IN ('Active', 'Inactive', 'Closed')");
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
                name: "Buddies",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuddyUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buddies", x => new { x.UserId, x.BuddyUserId });
                    table.CheckConstraint("ck_buddies_not_self", "\"UserId\" <> \"BuddyUserId\"");
                    table.ForeignKey(
                        name: "FK_Buddies_Users_BuddyUserId",
                        column: x => x.BuddyUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Buddies_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BuddyRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairLowId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairHighId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BlockedSender = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuddyRequests", x => x.Id);
                    table.CheckConstraint("ck_buddyrequests_not_self", "\"FromUserId\" <> \"ToUserId\"");
                    table.CheckConstraint("ck_buddyrequests_pair", "\"PairLowId\" < \"PairHighId\"");
                    table.CheckConstraint("ck_buddyrequests_source", "\"Source\" IN ('Search', 'Qr')");
                    table.CheckConstraint("ck_buddyrequests_status", "\"Status\" IN ('Pending', 'Accepted', 'Declined', 'Cancelled', 'Expired', 'ClosedByBlock', 'ClosedByDeletion')");
                    table.ForeignKey(
                        name: "FK_BuddyRequests_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BuddyRequests_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingKind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TravellerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeOfDay = table.Column<TimeOnly>(type: "time", nullable: true),
                    FromPlace = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ToPlace = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Detail = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ListedTotal = table.Column<int>(type: "integer", nullable: true),
                    AgreedTotal = table.Column<int>(type: "integer", nullable: true),
                    Guests = table.Column<short>(type: "smallint", nullable: true),
                    Seats = table.Column<short>(type: "smallint", nullable: true),
                    RoomNames = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PlacesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    CancelledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FinalAmount = table.Column<int>(type: "integer", nullable: true),
                    AdvanceReceived = table.Column<int>(type: "integer", nullable: true),
                    PaidInFull = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEntries", x => x.Id);
                    table.CheckConstraint("ck_calendarentries_advance", "\"AdvanceReceived\" IS NULL OR (\"AdvanceReceived\" BETWEEN 0 AND 9900000 AND (\"FinalAmount\" IS NULL OR \"AdvanceReceived\" <= \"FinalAmount\"))");
                    table.CheckConstraint("ck_calendarentries_autosnap", "\"Kind\" = 'Auto' OR (\"Guests\" IS NULL AND \"Seats\" IS NULL AND \"RoomNames\" IS NULL AND \"PlacesJson\" IS NULL AND \"CancelledBy\" IS NULL)");
                    table.CheckConstraint("ck_calendarentries_cancelledby", "\"CancelledBy\" IS NULL OR \"CancelledBy\" IN ('Traveller', 'Owner', 'Driver', 'Admin', 'System')");
                    table.CheckConstraint("ck_calendarentries_dates", "\"DateTo\" >= \"DateFrom\"");
                    table.CheckConstraint("ck_calendarentries_final", "\"FinalAmount\" IS NULL OR \"FinalAmount\" BETWEEN 1 AND 9900000");
                    table.CheckConstraint("ck_calendarentries_kind", "\"Kind\" IN ('Auto', 'Manual')");
                    table.CheckConstraint("ck_calendarentries_listingkind", "\"ListingKind\" IN ('Stay', 'Taxi')");
                    table.CheckConstraint("ck_calendarentries_manual", "\"Kind\" <> 'Manual' OR (length(btrim(\"Label\")) >= 1 AND NOT \"Cancelled\" AND (\"ListingKind\" <> 'Stay' OR (\"DateTo\" > \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 60)) AND (\"ListingKind\" <> 'Taxi' OR \"DateTo\" = \"DateFrom\"))");
                    table.CheckConstraint("ck_calendarentries_paid", "NOT \"PaidInFull\" OR \"FinalAmount\" IS NOT NULL");
                    table.CheckConstraint("ck_calendarentries_source", "((\"Kind\" = 'Auto') = (\"SourceType\" IS NOT NULL AND \"SourceId\" IS NOT NULL)) AND (\"SourceType\" IS NULL OR \"SourceType\" IN ('StayEnquiry', 'TaxiBooking', 'TaxiLead'))");
                    table.CheckConstraint("ck_calendarentries_time", "\"TimeOfDay\" IS NULL OR (\"ListingKind\" = 'Taxi' AND \"Kind\" = 'Manual')");
                    table.ForeignKey(
                        name: "FK_CalendarEntries_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEntries_Users_TravellerUserId",
                        column: x => x.TravellerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "FestivalReminderSent",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FestivalDateId = table.Column<Guid>(type: "uuid", nullable: false),
                    FestivalDateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FestivalReminderSent", x => new { x.UserId, x.FestivalDateId });
                    table.ForeignKey(
                        name: "FK_FestivalReminderSent_FestivalDates_FestivalDateId",
                        column: x => x.FestivalDateId,
                        principalTable: "FestivalDates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FestivalReminderSent_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Follows",
                columns: table => new
                {
                    FollowerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolloweeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "char(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Follows", x => new { x.FollowerId, x.FolloweeId });
                    table.CheckConstraint("ck_follows_not_self", "\"FollowerId\" <> \"FolloweeId\"");
                    table.CheckConstraint("ck_follows_status", "\"Status\" IN ('A', 'R')");
                    table.ForeignKey(
                        name: "FK_Follows_Users_FolloweeId",
                        column: x => x.FolloweeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Follows_Users_FollowerId",
                        column: x => x.FollowerId,
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
                name: "ProfileIdentityAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldGender = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    NewGender = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    OldBirthMonth = table.Column<short>(type: "smallint", nullable: false),
                    NewBirthMonth = table.Column<short>(type: "smallint", nullable: false),
                    OldBirthYear = table.Column<short>(type: "smallint", nullable: false),
                    NewBirthYear = table.Column<short>(type: "smallint", nullable: false),
                    OldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NewName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileIdentityAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileIdentityAudit_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    LoggedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokeReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
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
                    PendingEditJson = table.Column<string>(type: "jsonb", nullable: true),
                    PendingEditAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingEditRejectReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PendingEditResult = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    PendingEditResultAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SpecialPriceSavesDay = table.Column<DateOnly>(type: "date", nullable: true),
                    SpecialPriceSavesCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    PlanVisibleSavesDay = table.Column<DateOnly>(type: "date", nullable: true),
                    PlanVisibleSavesCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    RemindersOn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
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
                    table.CheckConstraint("ck_staylistings_pendingeditresult", "\"PendingEditResult\" IS NULL OR \"PendingEditResult\" IN ('Approved', 'Rejected')");
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
                    DisplayPhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DisplayPhotoPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    RatingSum = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RatingCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RidesCompleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RingReadyAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RingReadyBuild = table.Column<int>(type: "integer", nullable: true),
                    RingNotificationsOn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RingMissStreak = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    RingWeight = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    PlanVisibleSavesDay = table.Column<DateOnly>(type: "date", nullable: true),
                    PlanVisibleSavesCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    RemindersOn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Location = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography", stored: true),
                    LocationCoarse = table.Column<Point>(type: "geography(Point, 4326)", nullable: true, computedColumnSql: "ST_SetSRID(ST_MakePoint(round(\"Longitude\"::float8 / (0.018 / greatest(cos(radians(round(\"Latitude\"::float8 / 0.018) * 0.018)), 0.01))) * (0.018 / greatest(cos(radians(round(\"Latitude\"::float8 / 0.018) * 0.018)), 0.01)), round(\"Latitude\"::float8 / 0.018) * 0.018), 4326)::geography", stored: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiListings", x => x.Id);
                    table.CheckConstraint("ck_taxilistings_local_day_km_range", "\"LocalDayKm\" IS NULL OR (\"LocalDayKm\" >= 0 AND \"LocalDayKm\" <= 1000)");
                    table.CheckConstraint("ck_taxilistings_local_day_rate_range", "\"LocalDayRate\" IS NULL OR (\"LocalDayRate\" >= 100 AND \"LocalDayRate\" <= 9900000)");
                    table.CheckConstraint("ck_taxilistings_min_fare_range", "\"MinFare\" IS NULL OR (\"MinFare\" >= 100 AND \"MinFare\" <= 9900000)");
                    table.CheckConstraint("ck_taxilistings_plate", "\"NumberPlate\" IS NULL OR \"NumberPlate\" ~ '^([A-Z]{2}[0-9]{1,2}[A-Z]{1,3}[0-9]{4}|[0-9]{2}BH[0-9]{4}[A-Z]{1,2})$'");
                    table.CheckConstraint("ck_taxilistings_ringmiss", "\"RingMissStreak\" >= 0");
                    table.CheckConstraint("ck_taxilistings_ringweight", "\"RingWeight\" BETWEEN 1 AND 5");
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
                    NameChosen = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    InviteCode = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    DayCount = table.Column<int>(type: "integer", nullable: false),
                    SpotCount = table.Column<int>(type: "integer", nullable: false),
                    ClientTripId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionCountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PostDays = table.Column<short>(type: "smallint", nullable: true),
                    PostWindowMonths = table.Column<short>(type: "smallint", nullable: true),
                    PostStartMonth = table.Column<int>(type: "integer", nullable: true),
                    JoinGender = table.Column<string>(type: "char(1)", nullable: false, defaultValue: "A"),
                    Seats = table.Column<short>(type: "smallint", nullable: true),
                    PostCityNames = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    PostStateName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    InactiveByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PausedByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.CheckConstraint("ck_trips_citykey_positive", "\"CityKey\" > 0");
                    table.CheckConstraint("ck_trips_joingender", "\"JoinGender\" IN ('A', 'M', 'F')");
                    table.CheckConstraint("ck_trips_postdays", "\"PostDays\" IS NULL OR \"PostDays\" BETWEEN 1 AND 11");
                    table.CheckConstraint("ck_trips_postwindow", "\"PostWindowMonths\" IS NULL OR \"PostWindowMonths\" BETWEEN 1 AND 6");
                    table.CheckConstraint("ck_trips_public_fields", "NOT \"IsPublic\" OR (\"PublishedAt\" IS NOT NULL AND \"Seats\" IS NOT NULL AND \"PostDays\" IS NOT NULL)");
                    table.CheckConstraint("ck_trips_seats", "\"Seats\" IS NULL OR \"Seats\" BETWEEN 1 AND 11");
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
                name: "UserNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000")),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<int>(type: "integer", nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotices", x => x.Id);
                    table.CheckConstraint("ck_usernotices_count", "\"Count\" >= 1");
                    table.CheckConstraint("ck_usernotices_kind", "\"Kind\" IN ('BuddyRequest', 'TripInvite', 'PaymentSaidPaid', 'SettleReminder', 'PaymentNotReceived', 'ExpensesChanged', 'TripRemoved', 'TripDeleted', 'BuddyAccepted', 'NewBuddy', 'TripJoined', 'PlanUpdated', 'ExpenseAdded', 'ShareChanged', 'StayEditApproved', 'StayEditRejected', 'FestivalReminder', 'PlanLimitsChanged', 'PlansStarting', 'FollowRequest', 'NewFollower', 'FollowAccepted', 'JoinRequest', 'JoinAccepted', 'JoinDeclined', 'FollowedPosted')");
                    table.ForeignKey(
                        name: "FK_UserNotices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Gender = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    BirthMonth = table.Column<short>(type: "smallint", nullable: false),
                    BirthYear = table.Column<short>(type: "smallint", nullable: false),
                    About = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Interests = table.Column<string[]>(type: "text[]", nullable: false),
                    Skills = table.Column<string[]>(type: "text[]", nullable: false),
                    Places = table.Column<string[]>(type: "text[]", nullable: false),
                    HomeCityName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    HomeStateName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    IsPrivate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    MessagePolicy = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "Everyone"),
                    NotifyMessages = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NotifyFollows = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NotifyFollowedTrips = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    FollowerCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    FollowingCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    NameChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.UserId);
                    table.CheckConstraint("ck_userprofiles_birthmonth", "\"BirthMonth\" BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_userprofiles_birthyear", "\"BirthYear\" BETWEEN 1900 AND 2100");
                    table.CheckConstraint("ck_userprofiles_followers", "\"FollowerCount\" >= 0");
                    table.CheckConstraint("ck_userprofiles_following", "\"FollowingCount\" >= 0");
                    table.CheckConstraint("ck_userprofiles_gender", "\"Gender\" IN ('Male', 'Female', 'Other')");
                    table.CheckConstraint("ck_userprofiles_messagepolicy", "\"MessagePolicy\" IN ('Everyone', 'Following', 'Buddies')");
                    table.ForeignKey(
                        name: "FK_UserProfiles_Users_UserId",
                        column: x => x.UserId,
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
                name: "StayBlockedRanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StayListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayBlockedRanges", x => x.Id);
                    table.CheckConstraint("ck_stayblockedranges_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 89");
                    table.ForeignKey(
                        name: "FK_StayBlockedRanges_StayListings_StayListingId",
                        column: x => x.StayListingId,
                        principalTable: "StayListings",
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
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    HiddenByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
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
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    HiddenByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
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
                name: "StaySpecialPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StayListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Direction = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Percent = table.Column<short>(type: "smallint", nullable: false),
                    FestivalDateId = table.Column<Guid>(type: "uuid", nullable: true),
                    LabelCopy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PausedByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaySpecialPrices", x => x.Id);
                    table.CheckConstraint("ck_stayspecialprices_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 59");
                    table.CheckConstraint("ck_stayspecialprices_direction", "\"Direction\" IN ('Lower', 'Higher')");
                    table.CheckConstraint("ck_stayspecialprices_percent", "\"Percent\" % 5 = 0 AND ((\"Direction\" = 'Lower' AND \"Percent\" BETWEEN 5 AND 50) OR (\"Direction\" = 'Higher' AND \"Percent\" BETWEEN 5 AND 100))");
                    table.ForeignKey(
                        name: "FK_StaySpecialPrices_StayListings_StayListingId",
                        column: x => x.StayListingId,
                        principalTable: "StayListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxiDayOffs",
                columns: table => new
                {
                    TaxiListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiDayOffs", x => new { x.TaxiListingId, x.Date });
                    table.CheckConstraint("ck_taxidayoffs_reason", "\"Reason\" IS NULL OR char_length(\"Reason\") BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_TaxiDayOffs_TaxiListings_TaxiListingId",
                        column: x => x.TaxiListingId,
                        principalTable: "TaxiListings",
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
                    FromCityKey = table.Column<long>(type: "bigint", nullable: true),
                    ToCityKey = table.Column<long>(type: "bigint", nullable: true),
                    CityKeysChosen = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Fare = table.Column<int>(type: "integer", nullable: false),
                    TripType = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    HiddenByPlan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
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
                name: "ChatRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Kind = table.Column<string>(type: "char(1)", nullable: false),
                    PairLowId = table.Column<Guid>(type: "uuid", nullable: true),
                    PairHighId = table.Column<Guid>(type: "uuid", nullable: true),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChargedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastPreview = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatRooms", x => x.Id);
                    table.CheckConstraint("ck_chatrooms_kind", "\"Kind\" IN ('D', 'T')");
                    table.CheckConstraint("ck_chatrooms_pair", "(\"Kind\" = 'D' AND \"PairLowId\" IS NOT NULL AND \"PairHighId\" IS NOT NULL AND \"PairLowId\" < \"PairHighId\" AND \"TripId\" IS NULL) OR (\"Kind\" = 'T' AND \"TripId\" IS NOT NULL AND \"PairLowId\" IS NULL AND \"PairHighId\" IS NULL)");
                    table.CheckConstraint("ck_chatrooms_status", "\"Status\" IN ('Request', 'Active', 'Declined')");
                    table.ForeignKey(
                        name: "FK_ChatRooms_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
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
                name: "TaxiRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravellerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EndReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CancelChip = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AdminNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    WantPickup = table.Column<bool>(type: "boolean", nullable: false),
                    WantSightseeing = table.Column<bool>(type: "boolean", nullable: false),
                    WantDrop = table.Column<bool>(type: "boolean", nullable: false),
                    PickupCityKey = table.Column<long>(type: "bigint", nullable: true),
                    PickupCityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DropCityKey = table.Column<long>(type: "bigint", nullable: true),
                    DropCityName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DropSameAsPickup = table.Column<bool>(type: "boolean", nullable: false),
                    DispatchCityKey = table.Column<long>(type: "bigint", nullable: false),
                    MatchToCityKey = table.Column<long>(type: "bigint", nullable: false),
                    MatchFromCityKey = table.Column<long>(type: "bigint", nullable: true),
                    Seats = table.Column<short>(type: "smallint", nullable: false),
                    Travellers = table.Column<short>(type: "smallint", nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateTo = table.Column<DateOnly>(type: "date", nullable: false),
                    SightseeingDaysJson = table.Column<string>(type: "jsonb", nullable: false),
                    TripSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    AcceptTarget = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)3),
                    AcceptedCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    SelectedOfferId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    QueueWaitStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WaitRound = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    QueueExhaustedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiRequests", x => x.Id);
                    table.CheckConstraint("ck_taxirequests_accepts", "\"AcceptedCount\" >= 0 AND \"AcceptedCount\" <= \"AcceptTarget\" AND \"AcceptTarget\" <= 9");
                    table.CheckConstraint("ck_taxirequests_dates", "\"DateTo\" >= \"DateFrom\"");
                    table.CheckConstraint("ck_taxirequests_seats", "\"Seats\" IN (4, 6, 7, 12)");
                    table.CheckConstraint("ck_taxirequests_sections", "\"WantPickup\" OR \"WantSightseeing\" OR \"WantDrop\"");
                    table.CheckConstraint("ck_taxirequests_status", "\"Status\" IN ('Open', 'Collecting', 'Selected', 'Booked', 'InProgress', 'Completed', 'Cancelled', 'Expired')");
                    table.ForeignKey(
                        name: "FK_TaxiRequests_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxiRequests_Users_TravellerUserId",
                        column: x => x.TravellerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Category = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DayNumber = table.Column<short>(type: "smallint", nullable: false),
                    LinkKind = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaidByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitMode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    LeftoverUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedOpId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastOpId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripExpenses", x => x.Id);
                    table.CheckConstraint("ck_tripexpenses_amount", "\"Amount\" BETWEEN 1 AND 500000");
                    table.CheckConstraint("ck_tripexpenses_category", "\"Category\" IN ('Stay', 'Taxi', 'Food', 'Fuel', 'Tickets', 'Other')");
                    table.CheckConstraint("ck_tripexpenses_day", "\"DayNumber\" >= 1");
                    table.CheckConstraint("ck_tripexpenses_link", "(\"LinkKind\" IS NULL) = (\"LinkId\" IS NULL)");
                    table.CheckConstraint("ck_tripexpenses_linkkind", "\"LinkKind\" IS NULL OR \"LinkKind\" IN ('Stay', 'Taxi')");
                    table.CheckConstraint("ck_tripexpenses_splitmode", "\"SplitMode\" IN ('All', 'Chosen', 'Custom')");
                    table.ForeignKey(
                        name: "FK_TripExpenses_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripExpenseState",
                columns: table => new
                {
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ExpenseCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotalSpent = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    OpenPayments = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SaidPaidCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ReceivedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ReopenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReopenedExpenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripExpenseState", x => x.TripId);
                    table.ForeignKey(
                        name: "FK_TripExpenseState_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripExpenseSuggestionSkips",
                columns: table => new
                {
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripExpenseSuggestionSkips", x => new { x.TripId, x.SourceKind, x.SourceId });
                    table.ForeignKey(
                        name: "FK_TripExpenseSuggestionSkips_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripJoinRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Tier = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripJoinRequests", x => x.Id);
                    table.CheckConstraint("ck_tripjoinrequests_not_self", "\"OwnerUserId\" <> \"RequesterId\"");
                    table.CheckConstraint("ck_tripjoinrequests_status", "\"Status\" IN ('Waiting', 'Accepted', 'Declined', 'Cancelled', 'Expired', 'ClosedByBlock')");
                    table.CheckConstraint("ck_tripjoinrequests_tier", "\"Tier\" BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_TripJoinRequests_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripJoinRequests_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InviteExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpensePromptDoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    JoinedVia = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "Invite")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripMembers", x => x.Id);
                    table.CheckConstraint("ck_tripmembers_joinedvia", "\"JoinedVia\" IN ('Invite', 'Request')");
                    table.CheckConstraint("ck_tripmembers_status", "\"Status\" IN ('Invited', 'Joined', 'Declined', 'Cancelled', 'Expired', 'Left', 'Removed')");
                    table.ForeignKey(
                        name: "FK_TripMembers_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    SaidPaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SaidPaidByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripPayments", x => x.Id);
                    table.CheckConstraint("ck_trippayments_amount", "\"Amount\" BETWEEN 1 AND 5000000");
                    table.CheckConstraint("ck_trippayments_ended", "(\"State\" IN ('Withdrawn', 'NotReceived')) = (\"EndedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_trippayments_not_self", "\"FromUserId\" <> \"ToUserId\"");
                    table.CheckConstraint("ck_trippayments_received", "\"State\" = 'Received' OR \"ReceivedAt\" IS NULL");
                    table.CheckConstraint("ck_trippayments_state", "\"State\" IN ('SaidPaid', 'Received', 'Withdrawn', 'NotReceived')");
                    table.ForeignKey(
                        name: "FK_TripPayments_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
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
                name: "TripShortlistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravellerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepKey = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    JourneyId = table.Column<Guid>(type: "uuid", nullable: true),
                    LegIndex = table.Column<short>(type: "smallint", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ClosedReason = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemindedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FinalPushedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripShortlistItems", x => x.Id);
                    table.CheckConstraint("ck_shortlist_kind", "\"Kind\" IN ('Stay','Taxi','Car')");
                    table.CheckConstraint("ck_shortlist_state", "\"State\" IN ('Active','Sent','Closed','Booked')");
                    table.ForeignKey(
                        name: "FK_TripShortlistItems_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 85);

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
                name: "ChatMessages",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SystemKind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Masked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => new { x.RoomId, x.Seq });
                    table.CheckConstraint("ck_chatmessages_system", "\"SystemKind\" IS NULL OR \"SystemKind\" IN ('joined', 'left')");
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "ChatRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatRoomMembers",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastReadSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ListBucket = table.Column<string>(type: "char(1)", nullable: false),
                    ClearedUpToSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    JoinedSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LeftAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatRoomMembers", x => new { x.RoomId, x.UserId });
                    table.CheckConstraint("ck_chatmembers_bucket", "\"ListBucket\" IN ('P', 'T', 'R', 'X')");
                    table.CheckConstraint("ck_chatmembers_unread", "\"UnreadCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_ChatRoomMembers_ChatRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "ChatRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatRoomMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxiRequestOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EndReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Chip = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SeqNo = table.Column<int>(type: "integer", nullable: false),
                    OfferedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PushReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PushLate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmBy = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxiRequestOffers", x => x.Id);
                    table.CheckConstraint("ck_taxioffers_status", "\"Status\" IN ('Ringing', 'Accepted', 'Declined', 'Missed', 'Selected', 'NotSelected', 'NotAgreed', 'Confirmed', 'Withdrawn', 'Closed')");
                    table.ForeignKey(
                        name: "FK_TaxiRequestOffers_TaxiRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "TaxiRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripExpenseRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    OpId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    SharesAfterJson = table.Column<string>(type: "jsonb", nullable: false),
                    AmountAfter = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripExpenseRevisions", x => x.Id);
                    table.CheckConstraint("ck_tripexpenserevisions_kind", "\"Kind\" IN ('Added', 'Edited', 'SharedWith', 'Deleted')");
                    table.ForeignKey(
                        name: "FK_TripExpenseRevisions_TripExpenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "TripExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripExpenseShares",
                columns: table => new
                {
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Share = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripExpenseShares", x => new { x.ExpenseId, x.UserId });
                    table.CheckConstraint("ck_tripexpenseshares_share", "\"Share\" > 0");
                    table.ForeignKey(
                        name: "FK_TripExpenseShares_TripExpenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "TripExpenses",
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
                    TripEnquiryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportedUsername = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    Context = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    MessageSeq = table.Column<long>(type: "bigint", nullable: true)
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
                name: "MessageSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    TakenForUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AboutUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnchorSeq = table.Column<long>(type: "bigint", nullable: true),
                    MessagesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    PurgeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageSnapshots_ListingReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "ListingReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    CoverTaxiIds = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
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
                    CityLeg = table.Column<short>(type: "smallint", nullable: true),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOut = table.Column<DateOnly>(type: "date", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ContextRefreshCount = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FailReason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsUrgent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AcceptedOfferId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgreedTotal = table.Column<int>(type: "integer", nullable: true),
                    ListedStayTotal = table.Column<int>(type: "integer", nullable: true),
                    ListedStayBreakdownJson = table.Column<string>(type: "jsonb", nullable: true),
                    ConfirmedByTravellerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByOwnerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DriverFareEditCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
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
                    StepKey = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemindedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FinalPushedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionCountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripEnquiries", x => x.Id);
                    table.CheckConstraint("ck_tripenquiries_amounts", "(\"AgreedTotal\" IS NULL OR \"AgreedTotal\" >= 0) AND (\"DriverFare\" IS NULL OR \"DriverFare\" >= 0) AND (\"AgreedFare\" IS NULL OR \"AgreedFare\" >= 0) AND (\"ListedRateSnapshot\" IS NULL OR \"ListedRateSnapshot\" >= 0)");
                    table.CheckConstraint("ck_tripenquiries_kind", "\"Kind\" IN ('Stay','Taxi')");
                    table.CheckConstraint("ck_tripenquiries_listedstaytotal", "\"ListedStayTotal\" IS NULL OR (\"Kind\" = 'Stay' AND \"ListedStayTotal\" >= 100)");
                    table.CheckConstraint("ck_tripenquiries_status", "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller','NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Failed','Closed')");
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
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 85);

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
                    { new Guid("10000000-0000-0000-0000-000000000001"), "Plan trips, find stays and taxis", true, "SEEKER", "Traveller", 1 },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "List your stay", true, "STAY_OWNER", "Stay Owner", 4 },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "List your taxi", true, "TAXI_DRIVER", "Taxi Driver", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_AdminId_IsValid",
                table: "AdminDeviceTokens",
                columns: new[] { "AdminId", "IsValid" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminDeviceTokens_Token",
                table: "AdminDeviceTokens",
                column: "Token");

            migrationBuilder.CreateIndex(
                name: "ix_admindevicetokens_updated",
                table: "AdminDeviceTokens",
                column: "UpdatedAt");

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
                name: "ix_adminsessions_loggedout",
                table: "AdminSessions",
                column: "LoggedOutAt",
                filter: "\"IsActive\" = false");

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
                name: "IX_BannerDismissals_DismissedAt",
                table: "BannerDismissals",
                column: "DismissedAt");

            migrationBuilder.CreateIndex(
                name: "ix_buddies_buddy",
                table: "Buddies",
                column: "BuddyUserId");

            migrationBuilder.CreateIndex(
                name: "ix_buddies_user_unseen",
                table: "Buddies",
                column: "UserId",
                filter: "\"SeenAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_ended",
                table: "BuddyRequests",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_from_created",
                table: "BuddyRequests",
                columns: new[] { "FromUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_pair_created",
                table: "BuddyRequests",
                columns: new[] { "PairLowId", "PairHighId", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_pending_expires",
                table: "BuddyRequests",
                column: "ExpiresAt",
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_to",
                table: "BuddyRequests",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "ix_buddyrequests_to_pending",
                table: "BuddyRequests",
                columns: new[] { "ToUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_buddyrequests_from_client",
                table: "BuddyRequests",
                columns: new[] { "FromUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_buddyrequests_pair_open",
                table: "BuddyRequests",
                columns: new[] { "PairLowId", "PairHighId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_calendarentries_dateto",
                table: "CalendarEntries",
                column: "DateTo");

            migrationBuilder.CreateIndex(
                name: "ix_calendarentries_listing_datefrom",
                table: "CalendarEntries",
                columns: new[] { "ListingKind", "ListingId", "DateFrom", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_calendarentries_listing_dateto",
                table: "CalendarEntries",
                columns: new[] { "ListingKind", "ListingId", "DateTo", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "DateFrom", "Kind", "Cancelled" });

            migrationBuilder.CreateIndex(
                name: "ix_calendarentries_owner",
                table: "CalendarEntries",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_calendarentries_traveller",
                table: "CalendarEntries",
                column: "TravellerUserId",
                filter: "\"TravellerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_calendarentries_source",
                table: "CalendarEntries",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "\"Kind\" = 'Auto'");

            migrationBuilder.CreateIndex(
                name: "ix_chatmessages_expiry",
                table: "ChatMessages",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ux_chatmessages_client",
                table: "ChatMessages",
                columns: new[] { "RoomId", "SenderId", "ClientMessageId" },
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chatmembers_list",
                table: "ChatRoomMembers",
                columns: new[] { "UserId", "ListBucket", "SortAt", "RoomId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_chatmembers_unread",
                table: "ChatRoomMembers",
                column: "UserId",
                filter: "\"UnreadCount\" > 0")
                .Annotation("Npgsql:IndexInclude", new[] { "UnreadCount", "ListBucket" });

            migrationBuilder.CreateIndex(
                name: "ix_chatrooms_charged",
                table: "ChatRooms",
                columns: new[] { "RequestedByUserId", "ChargedAt" },
                descending: new[] { false, true },
                filter: "\"ChargedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chatrooms_lastmessage",
                table: "ChatRooms",
                column: "LastMessageAt",
                filter: "\"Kind\" = 'D'");

            migrationBuilder.CreateIndex(
                name: "ix_chatrooms_preview",
                table: "ChatRooms",
                column: "LastMessageAt",
                filter: "\"LastPreview\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_chatrooms_pair",
                table: "ChatRooms",
                columns: new[] { "PairLowId", "PairHighId" },
                unique: true,
                filter: "\"Kind\" = 'D'");

            migrationBuilder.CreateIndex(
                name: "ux_chatrooms_trip",
                table: "ChatRooms",
                column: "TripId",
                unique: true,
                filter: "\"Kind\" = 'T'");

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
                name: "ix_conversations_last_message",
                table: "Conversations",
                column: "LastMessageAt");

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
                name: "ix_deletedaccountrecords_username",
                table: "DeletedAccountRecords",
                column: "Username",
                filter: "\"Username\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_Token",
                table: "DeviceTokens",
                column: "Token");

            migrationBuilder.CreateIndex(
                name: "ix_devicetokens_updated",
                table: "DeviceTokens",
                column: "UpdatedAt");

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
                name: "ix_dueactions_done",
                table: "DueActions",
                column: "DoneAt",
                filter: "\"DoneAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_dueactions_due",
                table: "DueActions",
                columns: new[] { "DueAt", "Id" },
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_dueactions_lease",
                table: "DueActions",
                column: "LeaseUntil",
                filter: "\"State\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_dueactions_request",
                table: "DueActions",
                column: "RequestId",
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_dueactions_subject",
                table: "DueActions",
                columns: new[] { "SubjectId", "Kind", "RoundNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_festivalremindersent_dateto",
                table: "FestivalReminderSent",
                column: "FestivalDateTo");

            migrationBuilder.CreateIndex(
                name: "ix_festivalremindersent_festival",
                table: "FestivalReminderSent",
                column: "FestivalDateId");

            migrationBuilder.CreateIndex(
                name: "ix_follows_followers",
                table: "Follows",
                columns: new[] { "FolloweeId", "CreatedAt", "FollowerId" },
                descending: new[] { false, true, false },
                filter: "\"Status\" = 'A'");

            migrationBuilder.CreateIndex(
                name: "ix_follows_following",
                table: "Follows",
                columns: new[] { "FollowerId", "CreatedAt", "FolloweeId" },
                descending: new[] { false, true, false },
                filter: "\"Status\" = 'A'");

            migrationBuilder.CreateIndex(
                name: "ix_follows_requests",
                table: "Follows",
                columns: new[] { "FolloweeId", "CreatedAt", "FollowerId" },
                descending: new[] { false, true, false },
                filter: "\"Status\" = 'R'");

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
                name: "ix_listingreports_reporter_created",
                table: "ListingReports",
                columns: new[] { "ReporterUserId", "CreatedAt" },
                descending: new[] { false, true });

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
                name: "IX_ListingReports_Status_CreatedAt_Id",
                table: "ListingReports",
                columns: new[] { "Status", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_ListingReports_TripEnquiryId",
                table: "ListingReports",
                column: "TripEnquiryId");

            migrationBuilder.CreateIndex(
                name: "ix_listingreports_user_reported",
                table: "ListingReports",
                columns: new[] { "ReportedUserId", "CreatedAt" },
                descending: new[] { false, true },
                filter: "\"ListingType\" = 'User'");

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
                name: "ix_messagesnapshots_purge",
                table: "MessageSnapshots",
                column: "PurgeAt");

            migrationBuilder.CreateIndex(
                name: "ix_messagesnapshots_report",
                table: "MessageSnapshots",
                column: "ReportId",
                filter: "\"ReportId\" IS NOT NULL");

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
                name: "ix_notificationlogs_sent",
                table: "NotificationLogs",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_UserId_Type_SentAt",
                table: "NotificationLogs",
                columns: new[] { "UserId", "Type", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReads_NotificationId",
                table: "NotificationReads",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_due",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAt", "Id" },
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_published",
                table: "OutboxMessages",
                column: "PublishedAt",
                filter: "\"PublishedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_key",
                table: "OutboxMessages",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ownerweeklystats_day",
                table: "OwnerWeeklyStats",
                column: "Day");

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
                name: "ix_popularroutes_order",
                table: "PopularRoutes",
                columns: new[] { "SortOrder", "Id" });

            migrationBuilder.CreateIndex(
                name: "ux_popularroutes_pair",
                table: "PopularRoutes",
                columns: new[] { "FromCityKey", "ToCityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_profileidentityaudit_user",
                table: "ProfileIdentityAudit",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true });

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
                name: "ix_reportreasons_scope_name",
                table: "ReportReasons",
                columns: new[] { "Scope", "Name" },
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
                name: "ix_sessions_loggedout",
                table: "Sessions",
                column: "LoggedOutAt",
                filter: "\"IsActive\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ix_stayblockedranges_dateto",
                table: "StayBlockedRanges",
                column: "DateTo");

            migrationBuilder.CreateIndex(
                name: "ix_stayblockedranges_stay_dateto",
                table: "StayBlockedRanges",
                columns: new[] { "StayListingId", "DateTo", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "DateFrom" });

            migrationBuilder.CreateIndex(
                name: "ix_stayjobs_active_stay",
                table: "StayJobs",
                column: "StayListingId",
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_stayjobs_closed",
                table: "StayJobs",
                column: "ClosedAt",
                filter: "\"Status\" = 'Closed'");

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
                name: "ix_staylistings_pendingedit",
                table: "StayListings",
                columns: new[] { "PendingEditAt", "Id" },
                filter: "\"PendingEditJson\" IS NOT NULL AND \"IsDeleted\" = false");

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
                name: "ix_stayspecialprices_active_from",
                table: "StaySpecialPrices",
                columns: new[] { "DateFrom", "Id" },
                filter: "NOT \"IsPaused\" AND NOT \"PausedByPlan\"");

            migrationBuilder.CreateIndex(
                name: "ix_stayspecialprices_dateto",
                table: "StaySpecialPrices",
                column: "DateTo");

            migrationBuilder.CreateIndex(
                name: "ix_stayspecialprices_stay_dateto",
                table: "StaySpecialPrices",
                columns: new[] { "StayListingId", "DateTo", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StayTypes_Name",
                table: "StayTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_booked",
                table: "TaxiBookings",
                column: "BookedAt");

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_cancelled",
                table: "TaxiBookings",
                column: "CancelledAt",
                filter: "\"Status\" = 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_driver_active",
                table: "TaxiBookings",
                columns: new[] { "DriverListingId", "DateFrom", "DateTo" },
                filter: "\"Status\" IN ('Booked', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_driver_dates",
                table: "TaxiBookings",
                columns: new[] { "DriverListingId", "DateFrom" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_traveller",
                table: "TaxiBookings",
                columns: new[] { "TravellerUserId", "BookedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_taxibookings_trip",
                table: "TaxiBookings",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ux_taxibookings_request_active",
                table: "TaxiBookings",
                column: "RequestId",
                unique: true,
                filter: "\"Status\" IN ('Booked', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "ix_taxidayoffs_date",
                table: "TaxiDayOffs",
                columns: new[] { "Date", "TaxiListingId" });

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
                name: "ix_taxilistings_location_coarse_gist",
                table: "TaxiListings",
                column: "LocationCoarse")
                .Annotation("Npgsql:IndexMethod", "gist");

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
                name: "ix_taxiratings_driver",
                table: "TaxiRatings",
                columns: new[] { "DriverListingId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_taxioffers_driver_list",
                table: "TaxiRequestOffers",
                columns: new[] { "DriverListingId", "OfferedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_taxioffers_driver_selected",
                table: "TaxiRequestOffers",
                column: "DriverListingId",
                filter: "\"Status\" = 'Selected'");

            migrationBuilder.CreateIndex(
                name: "ix_taxioffers_request",
                table: "TaxiRequestOffers",
                columns: new[] { "RequestId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_taxioffers_driver_ringing",
                table: "TaxiRequestOffers",
                column: "DriverListingId",
                unique: true,
                filter: "\"Status\" = 'Ringing'");

            migrationBuilder.CreateIndex(
                name: "ux_taxioffers_request_driver",
                table: "TaxiRequestOffers",
                columns: new[] { "RequestId", "DriverListingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxirequests_closed",
                table: "TaxiRequests",
                column: "ClosedAt",
                filter: "\"ClosedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_taxirequests_created",
                table: "TaxiRequests",
                column: "CreatedAt")
                .Annotation("Npgsql:IndexInclude", new[] { "MatchFromCityKey", "MatchToCityKey" });

            migrationBuilder.CreateIndex(
                name: "ix_taxirequests_dispatch",
                table: "TaxiRequests",
                columns: new[] { "DispatchCityKey", "CreatedAt", "Id" },
                filter: "\"Status\" IN ('Open', 'Collecting')");

            migrationBuilder.CreateIndex(
                name: "ix_taxirequests_from_created",
                table: "TaxiRequests",
                columns: new[] { "MatchFromCityKey", "CreatedAt" })
                .Annotation("Npgsql:IndexInclude", new[] { "MatchToCityKey", "DropCityKey" });

            migrationBuilder.CreateIndex(
                name: "ix_taxirequests_traveller",
                table: "TaxiRequests",
                columns: new[] { "TravellerUserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_taxirequests_active_trip",
                table: "TaxiRequests",
                column: "TripId",
                unique: true,
                filter: "\"Status\" IN ('Open', 'Collecting', 'Selected', 'Booked', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "ux_taxirequests_client",
                table: "TaxiRequests",
                columns: new[] { "TravellerUserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_taxiroutegeometries_status",
                table: "TaxiRouteGeometries",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_taxiroutes_from_city",
                table: "TaxiRoutes",
                column: "FromCityKey",
                filter: "\"FromCityKey\" IS NOT NULL AND NOT \"HiddenByPlan\"")
                .Annotation("Npgsql:IndexInclude", new[] { "TaxiListingId" });

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
                name: "ix_taxiroutes_to_from_city",
                table: "TaxiRoutes",
                columns: new[] { "ToCityKey", "FromCityKey" },
                filter: "\"ToCityKey\" IS NOT NULL AND \"FromCityKey\" IS NOT NULL AND NOT \"HiddenByPlan\"")
                .Annotation("Npgsql:IndexInclude", new[] { "TaxiListingId" });

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
                name: "ix_tripenquiries_booked_silence",
                table: "TripEnquiries",
                column: "BookedByTravellerAt",
                filter: "\"Status\" = 'BookedByTraveller'");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_checkin_unconfirmed",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "\"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_closed_updated",
                table: "TripEnquiries",
                column: "UpdatedAt",
                filter: "(\"Status\" IN ('NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Closed') OR (\"Status\" = 'Failed' AND \"FailReason\" IN ('DriverDenied','ListingRemoved')))");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_confirmed_updated",
                table: "TripEnquiries",
                column: "UpdatedAt",
                filter: "\"Status\" = 'ConfirmedByBoth'");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_conversation",
                table: "TripEnquiries",
                column: "ConversationId",
                filter: "\"ConversationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Asked','Offered','Countered')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_faresent_expires",
                table: "TripEnquiries",
                column: "ExpiresAt",
                filter: "\"Status\" = 'FareSent'");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_final",
                table: "TripEnquiries",
                column: "TravellerUserId",
                filter: "\"Status\" = 'Expired' AND \"FinalPushedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_hostreminder",
                table: "TripEnquiries",
                column: "OwnerUserId",
                filter: "\"Status\" IN ('Asked','Countered')")
                .Annotation("Npgsql:IndexInclude", new[] { "RemindedOn", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_listing_status",
                table: "TripEnquiries",
                columns: new[] { "ListingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_open_taxi_unique",
                table: "TripEnquiries",
                columns: new[] { "TripId", "Kind", "ListingId", "CityLeg" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_open_unique",
                table: "TripEnquiries",
                columns: new[] { "TripId", "Kind", "ListingId", "Day" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NOT NULL");

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
                name: "ix_tripenquiries_settled",
                table: "TripEnquiries",
                columns: new[] { "TripId", "StepKey" },
                filter: "\"Status\" IN ('ConfirmedByBoth','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_traveller",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "Kind", "ListingId" },
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_trip",
                table: "TripEnquiries",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "NOT \"IsUrgent\" AND \"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ux_tripenquiries_client_request",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "ClientRequestId", "ListingId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tripexpenserevisions_expense_op",
                table: "TripExpenseRevisions",
                columns: new[] { "ExpenseId", "OpId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tripexpenserevisions_expense_version",
                table: "TripExpenseRevisions",
                columns: new[] { "ExpenseId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tripexpenses_trip",
                table: "TripExpenses",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "ix_tripexpenses_trip_day_live",
                table: "TripExpenses",
                columns: new[] { "TripId", "DayNumber", "CreatedAt", "Id" },
                descending: new[] { false, true, true, true },
                filter: "\"DeletedAt\" IS NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "Amount", "PaidByUserId" });

            migrationBuilder.CreateIndex(
                name: "ix_tripexpenses_trip_link",
                table: "TripExpenses",
                columns: new[] { "TripId", "LinkKind", "LinkId" },
                filter: "\"LinkId\" IS NOT NULL AND \"DeletedAt\" IS NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "Amount" });

            migrationBuilder.CreateIndex(
                name: "ix_tripexpenseshares_trip_user",
                table: "TripExpenseShares",
                columns: new[] { "TripId", "UserId" })
                .Annotation("Npgsql:IndexInclude", new[] { "Share" });

            migrationBuilder.CreateIndex(
                name: "ix_tripjoinrequests_ended",
                table: "TripJoinRequests",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripjoinrequests_requester",
                table: "TripJoinRequests",
                columns: new[] { "RequesterId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_tripjoinrequests_trip",
                table: "TripJoinRequests",
                columns: new[] { "TripId", "Tier", "CreatedAt" },
                descending: new[] { false, false, true },
                filter: "\"Status\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "ux_tripjoinrequests_live",
                table: "TripJoinRequests",
                columns: new[] { "TripId", "RequesterId" },
                unique: true,
                filter: "\"Status\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "ux_tripjoinrequests_requester_client",
                table: "TripJoinRequests",
                columns: new[] { "RequesterId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tripmembers_ended",
                table: "TripMembers",
                column: "EndedAt",
                filter: "\"Status\" IN ('Declined', 'Cancelled', 'Expired')");

            migrationBuilder.CreateIndex(
                name: "ix_tripmembers_invite_expires",
                table: "TripMembers",
                column: "InviteExpiresAt",
                filter: "\"Status\" = 'Invited'");

            migrationBuilder.CreateIndex(
                name: "ix_tripmembers_trip_status",
                table: "TripMembers",
                columns: new[] { "TripId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_tripmembers_user_joined",
                table: "TripMembers",
                columns: new[] { "UserId", "JoinedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"Status\" = 'Joined'");

            migrationBuilder.CreateIndex(
                name: "ix_tripmembers_user_status",
                table: "TripMembers",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_tripmembers_trip_user",
                table: "TripMembers",
                columns: new[] { "TripId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tripoffers_closed",
                table: "TripOffers",
                column: "CreatedAt",
                filter: "\"Status\" IN ('Expired','Declined','Superseded')");

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
                name: "ix_trippayments_ended",
                table: "TripPayments",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trippayments_trip_state",
                table: "TripPayments",
                columns: new[] { "TripId", "State" })
                .Annotation("Npgsql:IndexInclude", new[] { "FromUserId", "ToUserId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "ux_trippayments_pair_saidpaid",
                table: "TripPayments",
                columns: new[] { "TripId", "FromUserId", "ToUserId" },
                unique: true,
                filter: "\"State\" = 'SaidPaid'");

            migrationBuilder.CreateIndex(
                name: "ix_tripproblemlogs_enquiry",
                table: "TripProblemLogs",
                column: "TripEnquiryId",
                filter: "\"TripEnquiryId\" IS NOT NULL");

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
                name: "ix_trips_public",
                table: "Trips",
                columns: new[] { "PublishedAt", "Id" },
                descending: new bool[0],
                filter: "\"IsPublic\"");

            migrationBuilder.CreateIndex(
                name: "ix_trips_startdate",
                table: "Trips",
                column: "StartDate",
                filter: "\"StartDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_trips_invite_code",
                table: "Trips",
                column: "InviteCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_active_user",
                table: "TripShortlistItems",
                column: "TravellerUserId",
                filter: "\"State\" = 'Active'")
                .Annotation("Npgsql:IndexInclude", new[] { "RemindedOn", "ClosesAt", "TripId" });

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_closed_at",
                table: "TripShortlistItems",
                column: "ClosedAt",
                filter: "\"State\" IN ('Closed','Booked')");

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_closes",
                table: "TripShortlistItems",
                column: "ClosesAt",
                filter: "\"State\" IN ('Active','Sent')");

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_final",
                table: "TripShortlistItems",
                column: "TravellerUserId",
                filter: "\"State\" = 'Closed' AND \"ClosedReason\" = 'NoReply' AND \"FinalPushedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_listing",
                table: "TripShortlistItems",
                columns: new[] { "ListingId", "State" });

            migrationBuilder.CreateIndex(
                name: "ix_shortlist_trip_step",
                table: "TripShortlistItems",
                columns: new[] { "TripId", "StepKey" });

            migrationBuilder.CreateIndex(
                name: "ux_shortlist_active_listing",
                table: "TripShortlistItems",
                columns: new[] { "TripId", "StepKey", "ListingId" },
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_userblocks_blocked",
                table: "UserBlocks",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "ix_userblocks_blocker_created",
                table: "UserBlocks",
                columns: new[] { "BlockerId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserBlocks_BlockerId_BlockedId",
                table: "UserBlocks",
                columns: new[] { "BlockerId", "BlockedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usernotices_trip_kind",
                table: "UserNotices",
                columns: new[] { "TripId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "ix_usernotices_updated",
                table: "UserNotices",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_usernotices_user_unread",
                table: "UserNotices",
                column: "UserId",
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_usernotices_user_updated",
                table: "UserNotices",
                columns: new[] { "UserId", "UpdatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_usernotices_user_kind_trip",
                table: "UserNotices",
                columns: new[] { "UserId", "Kind", "TripId" },
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

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "Users",
                column: "Username",
                unique: true,
                filter: "\"Username\" IS NOT NULL");

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
                name: "FK_TripEnquiries_Trips_TripId",
                table: "TripEnquiries");

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
                name: "Buddies");

            migrationBuilder.DropTable(
                name: "BuddyRequests");

            migrationBuilder.DropTable(
                name: "CalendarEntries");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatRoomMembers");

            migrationBuilder.DropTable(
                name: "CouponRedemptions");

            migrationBuilder.DropTable(
                name: "DeletedAccountRecords");

            migrationBuilder.DropTable(
                name: "DeviceTokens");

            migrationBuilder.DropTable(
                name: "DriverDispatchStates");

            migrationBuilder.DropTable(
                name: "DueActions");

            migrationBuilder.DropTable(
                name: "FestivalReminderSent");

            migrationBuilder.DropTable(
                name: "Follows");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "MessageSnapshots");

            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "NotificationReads");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "OwnerReminderRuns");

            migrationBuilder.DropTable(
                name: "OwnerWeeklyStats");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PlanFeatures");

            migrationBuilder.DropTable(
                name: "PopularRoutes");

            migrationBuilder.DropTable(
                name: "ProfileIdentityAudit");

            migrationBuilder.DropTable(
                name: "QuestionTemplates");

            migrationBuilder.DropTable(
                name: "RoleChangeRequests");

            migrationBuilder.DropTable(
                name: "SavedJourneyLegs");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "StayBlockedRanges");

            migrationBuilder.DropTable(
                name: "StayJobs");

            migrationBuilder.DropTable(
                name: "StayPhotos");

            migrationBuilder.DropTable(
                name: "StayRooms");

            migrationBuilder.DropTable(
                name: "StaySpecialPrices");

            migrationBuilder.DropTable(
                name: "TaxiDayOffs");

            migrationBuilder.DropTable(
                name: "TaxiDispatchCities");

            migrationBuilder.DropTable(
                name: "TaxiRatings");

            migrationBuilder.DropTable(
                name: "TaxiRequestOffers");

            migrationBuilder.DropTable(
                name: "TaxiRouteGeometries");

            migrationBuilder.DropTable(
                name: "TripExpenseRevisions");

            migrationBuilder.DropTable(
                name: "TripExpenseShares");

            migrationBuilder.DropTable(
                name: "TripExpenseState");

            migrationBuilder.DropTable(
                name: "TripExpenseSuggestionSkips");

            migrationBuilder.DropTable(
                name: "TripJoinRequests");

            migrationBuilder.DropTable(
                name: "TripMembers");

            migrationBuilder.DropTable(
                name: "TripPayments");

            migrationBuilder.DropTable(
                name: "TripProblemLogs");

            migrationBuilder.DropTable(
                name: "TripPushLogs");

            migrationBuilder.DropTable(
                name: "TripReminderRuns");

            migrationBuilder.DropTable(
                name: "TripShortlistItems");

            migrationBuilder.DropTable(
                name: "UserBlocks");

            migrationBuilder.DropTable(
                name: "UserNotices");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "DistrictBanners");

            migrationBuilder.DropTable(
                name: "ChatRooms");

            migrationBuilder.DropTable(
                name: "PlanPurchases");

            migrationBuilder.DropTable(
                name: "FestivalDates");

            migrationBuilder.DropTable(
                name: "ListingReports");

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
                name: "TaxiBookings");

            migrationBuilder.DropTable(
                name: "TaxiRequests");

            migrationBuilder.DropTable(
                name: "TaxiRoutes");

            migrationBuilder.DropTable(
                name: "TripExpenses");

            migrationBuilder.DropTable(
                name: "Coupons");

            migrationBuilder.DropTable(
                name: "UserPlans");

            migrationBuilder.DropTable(
                name: "ReportReasons");

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
                name: "Trips");

            migrationBuilder.DropTable(
                name: "Districts");

            migrationBuilder.DropTable(
                name: "TripEnquiries");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "TripOffers");
        }
    }
}

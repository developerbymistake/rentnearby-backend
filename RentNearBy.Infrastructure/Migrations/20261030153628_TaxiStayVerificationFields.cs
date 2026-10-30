using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaxiStayVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                table: "TaxiListings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrivingLicenceNumberEnc",
                table: "TaxiListings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gstin",
                table: "StayListings",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNumber",
                table: "StayListings",
                type: "character varying(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationOtherName",
                table: "StayListings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegistrationTypeId",
                table: "StayListings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationTypeName",
                table: "StayListings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                table: "DeletedAccountRecords",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrivingLicenceNumberEnc",
                table: "DeletedAccountRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StayGstin",
                table: "DeletedAccountRecords",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StayRegistrationNumber",
                table: "DeletedAccountRecords",
                type: "character varying(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StayRegistrationOtherName",
                table: "DeletedAccountRecords",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StayRegistrationTypeName",
                table: "DeletedAccountRecords",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_StayListings_RegistrationTypeId",
                table: "StayListings",
                column: "RegistrationTypeId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_StayListings_StayRegistrationTypes_RegistrationTypeId",
                table: "StayListings",
                column: "RegistrationTypeId",
                principalTable: "StayRegistrationTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StayListings_StayRegistrationTypes_RegistrationTypeId",
                table: "StayListings");

            migrationBuilder.DropTable(
                name: "StayRegistrationTypes");

            migrationBuilder.DropIndex(
                name: "IX_StayListings_RegistrationTypeId",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "TaxiListings");

            migrationBuilder.DropColumn(
                name: "DrivingLicenceNumberEnc",
                table: "TaxiListings");

            migrationBuilder.DropColumn(
                name: "Gstin",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "RegistrationNumber",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "RegistrationOtherName",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "RegistrationTypeId",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "RegistrationTypeName",
                table: "StayListings");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "DeletedAccountRecords");

            migrationBuilder.DropColumn(
                name: "DrivingLicenceNumberEnc",
                table: "DeletedAccountRecords");

            migrationBuilder.DropColumn(
                name: "StayGstin",
                table: "DeletedAccountRecords");

            migrationBuilder.DropColumn(
                name: "StayRegistrationNumber",
                table: "DeletedAccountRecords");

            migrationBuilder.DropColumn(
                name: "StayRegistrationOtherName",
                table: "DeletedAccountRecords");

            migrationBuilder.DropColumn(
                name: "StayRegistrationTypeName",
                table: "DeletedAccountRecords");
        }
    }
}

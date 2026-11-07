using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhoneBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_host_token",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_hostnc",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_notme",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_open_taxi_unique",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_open_unique",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tripenquiries_status",
                table: "TripEnquiries");

            migrationBuilder.Sql("UPDATE \"TripEnquiries\" SET \"Status\" = 'CancelledByTraveller' WHERE \"Status\" = 'ConfirmedByTraveller';");
            migrationBuilder.Sql("DELETE FROM \"Messages\" m WHERE m.\"Type\" = 'trip_phone_booking' AND NOT EXISTS (SELECT 1 FROM \"Messages\" r WHERE r.\"RespondsToMessageId\" = m.\"Id\");");
            migrationBuilder.Sql("DELETE FROM \"NotificationEvents\" WHERE \"Type\" IN ('TripPhoneBookingRecorded','TripHostConfirmed','TripHostDenied','TripHostLinkAnswered','TripHostNotConfirmed');");
            migrationBuilder.Sql("DELETE FROM \"TripPushLogs\" WHERE \"DedupeKey\" LIKE 'hostnc:%';");

            migrationBuilder.DropColumn(
                name: "HostConfirmAnswer",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmAnsweredAt",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmAnsweredByUserId",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmAnsweredVia",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmLinkCount",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmTokenExpiresAt",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmTokenHash",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "HostConfirmTokenIssuedAt",
                table: "TripEnquiries");

            migrationBuilder.DropColumn(
                name: "PhoneBookingJson",
                table: "TripEnquiries");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller')");

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
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "Kind", "ListingId" },
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "NOT \"IsUrgent\" AND \"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tripenquiries_status",
                table: "TripEnquiries",
                sql: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller','NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Failed','Closed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_open_taxi_unique",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_open_unique",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries");

            migrationBuilder.DropIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tripenquiries_status",
                table: "TripEnquiries");

            migrationBuilder.AddColumn<string>(
                name: "HostConfirmAnswer",
                table: "TripEnquiries",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostConfirmAnsweredAt",
                table: "TripEnquiries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HostConfirmAnsweredByUserId",
                table: "TripEnquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostConfirmAnsweredVia",
                table: "TripEnquiries",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "HostConfirmLinkCount",
                table: "TripEnquiries",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostConfirmTokenExpiresAt",
                table: "TripEnquiries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostConfirmTokenHash",
                table: "TripEnquiries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostConfirmTokenIssuedAt",
                table: "TripEnquiries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneBookingJson",
                table: "TripEnquiries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_expires",
                table: "TripEnquiries",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

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
                columns: new[] { "TripId", "Kind", "ListingId", "CityLeg" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_open_unique",
                table: "TripEnquiries",
                columns: new[] { "TripId", "Kind", "ListingId", "Day" },
                unique: true,
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller') AND \"Day\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_traveller_listing",
                table: "TripEnquiries",
                columns: new[] { "TravellerUserId", "Kind", "ListingId" },
                filter: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.CreateIndex(
                name: "ix_tripenquiries_urgent",
                table: "TripEnquiries",
                column: "CheckIn",
                filter: "NOT \"IsUrgent\" AND \"CheckIn\" IS NOT NULL AND \"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tripenquiries_status",
                table: "TripEnquiries",
                sql: "\"Status\" IN ('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller','NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Failed','Closed')");
        }
    }
}

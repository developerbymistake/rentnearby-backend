using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyInquiryStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only backfill: InquiryStatuses dropped Confirmed/Cancelled/Rejected in favor of a
            // single terminal Closed status. There is no DB check constraint on this column to alter,
            // so any historical row still holding one of the removed values must be rewritten here or
            // it will silently stop being recognized by the app.
            migrationBuilder.Sql(
                "UPDATE \"Inquiries\" SET \"Status\" = 'Closed' WHERE \"Status\" IN ('Confirmed', 'Cancelled', 'Rejected');");
            migrationBuilder.Sql(
                "UPDATE \"InquiryStatusHistories\" SET \"Status\" = 'Closed' WHERE \"Status\" IN ('Confirmed', 'Cancelled', 'Rejected');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

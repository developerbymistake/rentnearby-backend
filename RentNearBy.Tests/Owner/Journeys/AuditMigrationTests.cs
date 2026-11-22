using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-W4 (D-29): the one fresh InitialCreate of the audit round carries the counter columns and checks, the session revoke reason,
// the five new indexes, and none of the nine dropped ones. MigrationShapeTests already pins "exactly one migration" and
// "model equals snapshot".
public class AuditMigrationTests
{
    private static string Migration()
    {
        var dir = Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Infrastructure", "Migrations");
        var file = Assert.Single(Directory.GetFiles(dir, "*_InitialCreate.cs"));
        return File.ReadAllText(file);
    }

    [Theory]
    [InlineData("CompletedTripsCount = table.Column<int>(type: \"integer\", nullable: false, defaultValue: 0)")]
    [InlineData("CompletedBookingsCount = table.Column<int>(type: \"integer\", nullable: false, defaultValue: 0)")]
    [InlineData("CompletionCountedAt = table.Column<DateTime>(type: \"timestamp with time zone\", nullable: true)")]
    [InlineData("RevokeReason = table.Column<string>(type: \"character varying(32)\", maxLength: 32, nullable: true)")]
    public void Fresh_initial_create_has_the_new_columns(string column) => Assert.Contains(column, Migration());

    [Theory]
    [InlineData("ck_users_completedtrips_nonneg")]
    [InlineData("ck_users_completedbookings_nonneg")]
    [InlineData("ix_tripenquiries_confirmed_updated")]
    [InlineData("ix_taxibookings_booked")]
    [InlineData("ix_taxibookings_cancelled")]
    [InlineData("ix_taxirequests_from_created")]
    [InlineData("ix_taxiroutes_from_city")]
    [InlineData("ix_taxibookings_trip")]
    public void Fresh_initial_create_has_the_audit_schema(string name) => Assert.Contains($"\"{name}\"", Migration());

    [Theory]
    [InlineData("ix_calendarentries_manual_active")]
    [InlineData("ix_stayspecialprices_bigoff")]
    [InlineData("ix_festivaldates_active_from")]
    [InlineData("ix_festivaldates_from_id")]
    [InlineData("IX_DeviceTokens_UserId")]
    [InlineData("IX_AdminDeviceTokens_AdminId")]
    [InlineData("IX_AdminDeviceTokens_IsValid")]
    [InlineData("IX_NotificationLogs_UserId")]
    [InlineData("IX_ListingReports_Status")]
    public void Dropped_indexes_are_gone(string name) => Assert.DoesNotContain($"name: \"{name}\"", Migration());

    [Fact]
    public void Kept_indexes_stay() => Assert.Contains("\"ix_stayspecialprices_active_from\"", Migration());
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 6.3 / R-4 (always runs, reflection over the EF model): every table with a foreign key to Users, StayListings or
// TaxiListings, or an FK-less *UserId / *ListingId column, is listed in AccountDeletionCoverage.Tables with a decided mode.
// A new table with such a column breaks this test until someone decides how account deletion treats it.
public class DeletionCoverageGuardTests
{
    private static readonly Type[] Principals = [typeof(User), typeof(StayListing), typeof(TaxiListing)];

    private static IModel Model()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEnumerable<IEntityType> Tables(IModel model)
        => model.GetEntityTypes().Where(e => e.GetTableName() != null && e.BaseType == null && !e.IsOwned());

    private static bool HasUserOrListingKey(IEntityType e)
    {
        var fkProps = e.GetForeignKeys().SelectMany(f => f.Properties).Select(p => p.Name).ToHashSet();
        return e.GetForeignKeys().Any(f => Principals.Contains(f.PrincipalEntityType.ClrType))
            || e.GetProperties().Any(p => !fkProps.Contains(p.Name)
                && (p.Name.EndsWith("UserId", StringComparison.Ordinal) || p.Name.EndsWith("ListingId", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_table_with_a_user_or_listing_key_has_a_decided_mode()
    {
        var model = Model();
        var listed = AccountDeletionCoverage.Tables.Select(t => t.Table).ToHashSet(StringComparer.Ordinal);
        var missing = Tables(model).Where(HasUserOrListingKey).Select(e => e.GetTableName()!).Where(t => !listed.Contains(t)).Distinct().OrderBy(t => t).ToList();
        Assert.True(missing.Count == 0, "Not in AccountDeletionCoverage.Tables: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_listed_table_exists_and_has_a_reason()
    {
        var names = Tables(Model()).Select(e => e.GetTableName()!).ToHashSet(StringComparer.Ordinal);
        Assert.All(AccountDeletionCoverage.Tables, t =>
        {
            Assert.True(names.Contains(t.Table), $"{t.Table} is not a table of the model");
            Assert.NotEqual(DeletionModes.None, t.Mode);
            Assert.False(string.IsNullOrWhiteSpace(t.Reason), $"{t.Table} has no reason");
        });
        Assert.Equal(AccountDeletionCoverage.Tables.Count, AccountDeletionCoverage.Tables.Select(t => t.Table).Distinct().Count());
    }

    [Fact]
    public void Cascade_mode_is_true_in_the_model()
    {
        var byTable = Tables(Model()).ToDictionary(e => e.GetTableName()!);
        Assert.All(AccountDeletionCoverage.Tables.Where(t => t.Mode.HasFlag(DeletionModes.Cascade)), t =>
            Assert.True(byTable[t.Table].GetForeignKeys().Any(f => Principals.Contains(f.PrincipalEntityType.ClrType)
                && f.DeleteBehavior == DeleteBehavior.Cascade), $"{t.Table} is marked Cascade but no user or listing foreign key cascades"));
    }

    [Fact]
    public void Owner_tables_follow_contract_au26()
    {
        var c = AccountDeletionCoverage.Tables.ToDictionary(t => t.Table);
        Assert.Equal(DeletionModes.Purge | DeletionModes.Cascade | DeletionModes.Anonymised, c["CalendarEntries"].Mode);
        Assert.Equal(DeletionModes.Purge | DeletionModes.Cascade, c["StayBlockedRanges"].Mode);
        Assert.Equal(DeletionModes.Purge | DeletionModes.Cascade, c["StaySpecialPrices"].Mode);
        Assert.Equal(DeletionModes.Purge | DeletionModes.Cascade, c["FestivalReminderSent"].Mode);
        Assert.Equal(DeletionModes.Purge, c["OwnerWeeklyStats"].Mode);
        Assert.Equal(DeletionModes.Cascade, c["UserNotices"].Mode);
        Assert.True(c["StayPhotos"].Mode.HasFlag(DeletionModes.Purge));
        Assert.True(c["StayRooms"].Mode.HasFlag(DeletionModes.Purge));
        Assert.True(c["TaxiRoutes"].Mode.HasFlag(DeletionModes.Purge));
        Assert.True(c["TripEnquiries"].Mode.HasFlag(DeletionModes.Anonymised));
        Assert.True(c["TaxiBookings"].Mode.HasFlag(DeletionModes.Anonymised));
        Assert.Equal(DeletionModes.Purge, c["OutboxMessages"].Mode);
    }
}

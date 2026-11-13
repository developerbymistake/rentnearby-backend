using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Entities;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Perf;

public class PerfIndexModelTests
{
    private static IModel Model()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IIndex Index<T>(string name)
    {
        var entity = Model().FindEntityType(typeof(T))!;
        var matches = entity.GetIndexes().Where(i => i.GetDatabaseName() == name).ToList();
        Assert.Single(matches);
        return matches[0];
    }

    private static string[] Columns(IIndex index) => index.Properties.Select(p => p.Name).ToArray();

    [Fact]
    public void BookedSilenceIndex_IsPartialOnBookedByTravellerAt()
    {
        var ix = Index<TripEnquiry>("ix_tripenquiries_booked_silence");
        Assert.Equal(new[] { "BookedByTravellerAt" }, Columns(ix));
        Assert.Equal("\"Status\" = 'BookedByTraveller'", ix.GetFilter());
        Assert.False(ix.IsUnique);
    }

    [Fact]
    public void FareSentExpiresIndex_IsPartialOnExpiresAt()
    {
        var ix = Index<TripEnquiry>("ix_tripenquiries_faresent_expires");
        Assert.Equal(new[] { "ExpiresAt" }, Columns(ix));
        Assert.Equal("\"Status\" = 'FareSent'", ix.GetFilter());
        Assert.False(ix.IsUnique);
    }

    [Fact]
    public void ListingStatusIndex_IsPlainListingIdThenStatus()
    {
        var ix = Index<TripEnquiry>("ix_tripenquiries_listing_status");
        Assert.Equal(new[] { "ListingId", "Status" }, Columns(ix));
        Assert.Null(ix.GetFilter());
        Assert.False(ix.IsUnique);
    }

    [Fact]
    public void ShortlistListingIndex_IsPlainListingIdThenState()
    {
        var ix = Index<TripShortlistItem>("ix_shortlist_listing");
        Assert.Equal(new[] { "ListingId", "State" }, Columns(ix));
        Assert.Null(ix.GetFilter());
        Assert.False(ix.IsUnique);
    }

    [Fact]
    public void ReporterCreatedIndex_IsReporterThenCreatedAtDescending()
    {
        var ix = Index<ListingReport>("ix_listingreports_reporter_created");
        Assert.Equal(new[] { "ReporterUserId", "CreatedAt" }, Columns(ix));
        Assert.Equal(new[] { false, true }, ix.IsDescending);
        Assert.Null(ix.GetFilter());
        Assert.False(ix.IsUnique);
    }

    [Fact]
    public void ReporterPendingUniqueIndex_IsUnchanged()
    {
        var ix = Index<ListingReport>("ix_listingreports_reporter_listing_pending");
        Assert.Equal(new[] { "ReporterUserId", "ListingId" }, Columns(ix));
        Assert.True(ix.IsUnique);
        Assert.Equal("\"Status\" = 'Pending'", ix.GetFilter());
    }
}

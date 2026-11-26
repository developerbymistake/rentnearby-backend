using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.TaxiRequests;
using RentNearBy.Tests.Taxis;

namespace RentNearBy.Tests.TaxiAvailability;

// Contract 1.9: only a booking move (confirm, start, complete, cancel, end with a booking) bumps taxis:avail:gen.
public class TaxiAvailGenBumpTests
{
    private static TaxiRequestStore Store(out List<string> keys)
    {
        var redis = TaxiPhotoIntegrationTests.RecordingRedis.Create(out keys);
        return new TaxiRequestStore(null!, null!, null!, null!, null!, null!, NullLogger<TaxiRequestStore>.Instance, null, redis);
    }

    private static TaxiClosureResult Changed(string state)
        => new([1], [], [new TaxiRequestChange(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, state)]);

    [Theory]
    [InlineData(RequestStates.Collecting)]
    [InlineData(RequestStates.Selected)]
    [InlineData(RequestStates.Open)]
    public async Task A_request_change_without_a_booking_move_does_not_bump(string state)
    {
        var store = Store(out var keys);
        await store.AfterCommitAsync(Changed(state));
        Assert.DoesNotContain(ListingCacheKeys.TaxiAvailGen, keys);
    }

    [Fact]
    public async Task A_booking_move_bumps_once_and_the_flag_resets()
    {
        var store = Store(out var keys);
        store.MarkAvailabilityMoved();
        await store.AfterCommitAsync(Changed(RequestStates.Booked));
        Assert.Equal(1, keys.Count(k => k == ListingCacheKeys.TaxiAvailGen));
        Assert.False(store.AvailabilityMoved);

        await store.AfterCommitAsync(Changed(RequestStates.Collecting));
        Assert.Equal(1, keys.Count(k => k == ListingCacheKeys.TaxiAvailGen));
    }

    [Fact]
    public void Every_booking_writer_of_the_store_sets_the_flag()
    {
        var src = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestStore.cs");
        Assert.Contains("if (to == RequestStates.Booked) _availabilityMoved = true;", src);
        Assert.Contains("if (moved is not null) _availabilityMoved = true;", src);
        Assert.Contains("if (booking is not null) _availabilityMoved = true;", src);
        Assert.DoesNotContain("if (result.Changes.Count > 0)", src);
    }
}

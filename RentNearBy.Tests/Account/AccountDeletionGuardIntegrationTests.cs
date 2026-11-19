using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.Account;

// Owner row O17 on a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class AccountDeletionGuardIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    // Row purge only; Cloudinary is never called here.
    private sealed class RowPurge(ApplicationDbContext db) : IListingPurgeService
    {
        private readonly ListingPurgeService _inner = new(db, null!, NullLogger<ListingPurgeService>.Instance);

        public async Task<(IReadOnlyList<Guid> StayIds, IReadOnlyList<Guid> TaxiIds)> PurgeUserListingsAsync(Guid userId)
            => (await _inner.DeleteUserListingRowsAsync(userId, ListingKinds.Stay), await _inner.DeleteUserListingRowsAsync(userId, ListingKinds.Taxi));

        public Task<IReadOnlyList<Guid>> DeleteUserListingRowsAsync(Guid userId, string listingKind) => _inner.DeleteUserListingRowsAsync(userId, listingKind);
        public Task DeleteListingPhotosBestEffortAsync(Guid userId, IReadOnlyList<Guid> listingIds) => Task.CompletedTask;
    }

    private static async Task<AccountDeletionResult> DeleteAsync(JourneyHarness h, Guid userId)
    {
        await using var s = h.NewScope();
        var service = new AccountDeletionService(s.Db, new RowPurge(s.Db), new PlaySubscriptionRepository(s.Db),
            Options.Create(new PlayBillingOptions()), s.Closure,
            new ListingCacheInvalidator(s.Db, h.Services, NullLogger<ListingCacheInvalidator>.Instance), h.Services);
        return await service.DeleteAccountAsync(userId);
    }

    private static IEnumerable<string> Codes(AccountDeletionResult r) => r.Reasons.Select(b => b.Code);

    private async Task<bool> UserExistsAsync(Guid id)
    {
        await using var read = CreateContext();
        return await read.Users.AnyAsync(u => u.Id == id);
    }

    private async Task<int> RecordCountAsync(Guid id)
    {
        await using var read = CreateContext();
        return await read.DeletedAccountRecords.CountAsync(r => r.OriginalUserId == id);
    }

    private async Task CleanupAsync(params Guid[] userIds)
    {
        await using var c = CreateContext();
        await c.DeletedAccountRecords.Where(r => userIds.Contains(r.OriginalUserId)).ExecuteDeleteAsync();
        await c.Set<TaxiBooking>().Where(b => userIds.Contains(b.TravellerUserId) || userIds.Contains(b.DriverUserId)).ExecuteDeleteAsync();
    }

    private static async Task PauseTaxiAsync(JourneyHarness h, Guid listingId)
    {
        await using var s = h.NewScope();
        await s.Db.TaxiListings.Where(t => t.Id == listingId).ExecuteUpdateAsync(u => u.SetProperty(t => t.IsAvailable, false));
    }

    private static async Task PauseStayAsync(JourneyHarness h, Guid listingId)
    {
        await using var s = h.NewScope();
        await s.Db.StayListings.Where(t => t.Id == listingId).ExecuteUpdateAsync(u => u.SetProperty(t => t.AcceptingGuests, false));
    }

    private static async Task<Guid> AddBookingAsync(JourneyHarness h, Guid traveller, Guid driver, Guid listing, string status)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiBooking>().Add(new TaxiBooking
        {
            Id = id, RequestId = Guid.NewGuid(), OfferId = Guid.NewGuid(), TripId = Guid.NewGuid(), DriverListingId = listing,
            DriverUserId = driver, TravellerUserId = traveller, Status = status, DriverName = "Ravi", TravellerName = "Asha",
            DateFrom = h.Start, DateTo = h.Start.AddDays(2), BookedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> AddRequestAsync(JourneyHarness h, Guid traveller, string status)
    {
        var tripId = await h.AddTripAsync(traveller, h.BuildPlan(("Nainital", 210000001, 3)));
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = id, TripId = tripId, TravellerUserId = traveller, Status = status, WantSightseeing = true,
            DispatchCityKey = 210000001, MatchToCityKey = 210000001, Seats = 4, Travellers = 2,
            DateFrom = h.Start, DateTo = h.Start.AddDays(2), CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static async Task AddEnquiryAsync(JourneyHarness h, Guid traveller, Guid owner, Guid listing, string kind, string status, DateOnly? checkOut)
    {
        var tripId = await h.AddTripAsync(traveller, h.BuildPlan(("Nainital", 210000001, 3)));
        await using var s = h.NewScope();
        s.Db.TripEnquiries.Add(new TripEnquiry
        {
            Id = Guid.NewGuid(), TripId = tripId, TravellerUserId = traveller, OwnerUserId = owner, Kind = kind, ListingId = listing,
            ListingName = "Listing", Status = status, Day = kind == TripEnquiryKinds.Stay ? (short)1 : null,
            CheckIn = checkOut?.AddDays(-2), CheckOut = checkOut, ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
    }

    [IntegrationFact]
    public async Task Traveller_with_booked_booking_is_refused_and_nothing_is_written()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);
        await AddBookingAsync(h, traveller, driver, listing, BookingStates.Booked);

        var result = await DeleteAsync(h, traveller);

        Assert.Equal([AccountDeletionReasons.ActiveTaxiBooking], Codes(result));
        Assert.True(await UserExistsAsync(traveller));
        Assert.Equal(0, await RecordCountAsync(traveller));
        await CleanupAsync(traveller, driver);
    }

    [IntegrationFact]
    public async Task Driver_with_inprogress_booking_is_refused_even_when_paused()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);
        await PauseTaxiAsync(h, listing);
        await AddBookingAsync(h, traveller, driver, listing, BookingStates.InProgress);

        Assert.Equal([AccountDeletionReasons.ActiveTaxiBooking], Codes(await DeleteAsync(h, driver)));
        Assert.True(await UserExistsAsync(driver));
        await CleanupAsync(traveller, driver);
    }

    [IntegrationFact]
    public async Task Driver_with_selected_offer_is_refused_and_accepted_offer_is_not()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);
        await PauseTaxiAsync(h, listing);
        var requestId = await AddRequestAsync(h, traveller, RequestStates.Selected);
        var offerId = Guid.NewGuid();
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
            {
                Id = offerId, RequestId = requestId, DriverListingId = listing, DriverUserId = driver, Status = OfferStates.Selected,
                SeqNo = 1, OfferedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5), ConfirmBy = DateTime.UtcNow.AddMinutes(15),
            });
            await s.Db.SaveChangesAsync();
        }

        Assert.Equal([AccountDeletionReasons.SelectedTaxiOffer], Codes(await DeleteAsync(h, driver)));

        await using (var s = h.NewScope())
            await s.Db.Set<TaxiRequestOffer>().Where(o => o.Id == offerId).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OfferStates.Accepted));
        Assert.True((await DeleteAsync(h, driver)).Deleted);
        Assert.False(await UserExistsAsync(driver));
        await CleanupAsync(traveller, driver);
    }

    [IntegrationFact]
    public async Task Traveller_with_open_request_is_refused()
    {
        await using var h = new JourneyHarness(Db);
        foreach (var status in new[] { RequestStates.Open, RequestStates.Collecting, RequestStates.Selected })
        {
            var traveller = await h.AddSeekerAsync();
            await AddRequestAsync(h, traveller, status);
            Assert.Equal([AccountDeletionReasons.OpenTaxiRequest], Codes(await DeleteAsync(h, traveller)));
            Assert.True(await UserExistsAsync(traveller));
        }
    }

    [IntegrationFact]
    public async Task Driver_with_live_listing_is_refused_and_paused_listing_is_not()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);

        Assert.Equal([AccountDeletionReasons.LiveListing], Codes(await DeleteAsync(h, driver)));

        await PauseTaxiAsync(h, listing);
        Assert.True((await DeleteAsync(h, driver)).Deleted);
        Assert.False(await UserExistsAsync(driver));
        await using var read = CreateContext();
        var record = await read.DeletedAccountRecords.SingleAsync(r => r.OriginalUserId == driver);
        Assert.Equal(UserRoles.TaxiDriver, record.Role);
        Assert.NotNull(record.DrivingLicenceNumberEnc);
        await CleanupAsync(driver);
    }

    [IntegrationFact]
    public async Task Stay_owner_accepting_guests_is_refused_and_paused_is_not()
    {
        await using var h = new JourneyHarness(Db);
        var owner = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(owner);

        Assert.Equal([AccountDeletionReasons.LiveListing], Codes(await DeleteAsync(h, owner)));

        await PauseStayAsync(h, stay);
        Assert.True((await DeleteAsync(h, owner)).Deleted);
        await using var read = CreateContext();
        Assert.Equal(UserRoles.StayOwner, (await read.DeletedAccountRecords.SingleAsync(r => r.OriginalUserId == owner)).Role);
        await CleanupAsync(owner);
    }

    [IntegrationFact]
    public async Task Stay_booking_active_until_checkout_day_then_free()
    {
        await using var h = new JourneyHarness(Db);
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
        var traveller = await h.AddSeekerAsync();
        var owner = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(owner);
        await PauseStayAsync(h, stay);
        await AddEnquiryAsync(h, traveller, owner, stay, TripEnquiryKinds.Stay, TripEnquiryStatuses.ConfirmedByBoth, today);

        Assert.Equal([AccountDeletionReasons.ActiveStayBooking], Codes(await DeleteAsync(h, owner)));
        Assert.Equal([AccountDeletionReasons.ActiveStayBooking], Codes(await DeleteAsync(h, traveller)));

        var past = await h.AddSeekerAsync();
        await AddEnquiryAsync(h, past, owner, stay, TripEnquiryKinds.Stay, TripEnquiryStatuses.ConfirmedByBoth, today.AddDays(-1));
        Assert.True((await DeleteAsync(h, past)).Deleted);
        await CleanupAsync(traveller, owner, past);
    }

    [IntegrationFact]
    public async Task Open_stay_enquiry_blocks_both_sides()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var owner = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(owner);
        await PauseStayAsync(h, stay);
        await AddEnquiryAsync(h, traveller, owner, stay, TripEnquiryKinds.Stay, TripEnquiryStatuses.Asked, h.Start.AddDays(2));

        Assert.Equal([AccountDeletionReasons.OpenStayEnquiry], Codes(await DeleteAsync(h, traveller)));
        Assert.Equal([AccountDeletionReasons.OpenStayEnquiry], Codes(await DeleteAsync(h, owner)));
    }

    [IntegrationFact]
    public async Task Legacy_taxi_lead_blocks_traveller_only()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);
        await PauseTaxiAsync(h, listing);
        await AddEnquiryAsync(h, traveller, driver, listing, TripEnquiryKinds.Taxi, TripEnquiryStatuses.Asked, h.Start.AddDays(3));

        Assert.Equal([AccountDeletionReasons.OpenTaxiRequest], Codes(await DeleteAsync(h, traveller)));
        Assert.True((await DeleteAsync(h, driver)).Deleted);
        await CleanupAsync(traveller, driver);
    }

    [IntegrationFact]
    public async Task Nothing_active_deletes_and_renames_bookings()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var listing = await h.AddTaxiAsync(driver);
        var booking = await AddBookingAsync(h, traveller, driver, listing, BookingStates.Completed);
        await using (var s = h.NewScope())
        {
            s.Db.Set<TaxiRating>().Add(new TaxiRating { BookingId = booking, DriverListingId = listing, Stars = 5, CreatedAt = DateTime.UtcNow });
            await s.Db.SaveChangesAsync();
        }

        Assert.True((await DeleteAsync(h, traveller)).Deleted);
        await using (var read = CreateContext())
        {
            var row = await read.Set<TaxiBooking>().SingleAsync(b => b.Id == booking);
            Assert.Equal("Deleted user", row.TravellerName);
            Assert.Equal("Ravi", row.DriverName);
            Assert.True(await read.Set<TaxiRating>().AnyAsync(r => r.BookingId == booking));
            Assert.Equal(UserRoles.Seeker, (await read.DeletedAccountRecords.SingleAsync(r => r.OriginalUserId == traveller)).Role);
        }

        await PauseTaxiAsync(h, listing);
        Assert.True((await DeleteAsync(h, driver)).Deleted);
        await using (var read = CreateContext())
            Assert.Equal("Deleted user", (await read.Set<TaxiBooking>().SingleAsync(b => b.Id == booking)).DriverName);

        await using (var c = CreateContext())
            await c.Set<TaxiRating>().Where(r => r.BookingId == booking).ExecuteDeleteAsync();
        await CleanupAsync(traveller, driver);
    }

    [IntegrationFact]
    public async Task Second_delete_after_success_is_a_no_op()
    {
        await using var h = new JourneyHarness(Db);
        var traveller = await h.AddSeekerAsync();

        Assert.True((await DeleteAsync(h, traveller)).Deleted);
        Assert.True((await DeleteAsync(h, traveller)).Deleted);
        Assert.Equal(1, await RecordCountAsync(traveller));
        await CleanupAsync(traveller);
    }
}

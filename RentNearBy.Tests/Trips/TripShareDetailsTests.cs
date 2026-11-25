using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Trips;

// POST /trips/enquiries/{id}/share-details on PostgreSQL (RENTNEARBY_TEST_DB); skipped without it.
public class TripShareDetailsTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long City = 210000001;

    private static string? ErrorTypeOf(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;

    private static async Task<IResult> ShareAsync(JourneyHarness h, Guid enquiryId, Guid callerId)
    {
        await using var s = h.NewScope();
        return await TripOwnerHandlers.ShareDetails(enquiryId, Principal(callerId), s.Uow, s.Db, h.RateLimit, h.Chat, s.Http);
    }

    [IntegrationFact]
    public async Task Host_shares_only_on_a_confirmed_booking_and_only_with_details_set()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", City, 2)));
        var ask = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay] });
        var enquiry = Assert.Single(DataOf<TripEnquiryBatchDto>(ask).Items);

        var offer = await h.SendOfferAsync(enquiry.Id, host, new SendTripOfferRequest
        {
            CheckIn = h.Start, CheckOut = h.Start.AddDays(2),
            Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
        });
        var early = await ShareAsync(h, enquiry.Id, host);
        Assert.Equal((409, TripEnquiryErrorTypes.TripEnquiryClosed), (StatusOf(early), ErrorTypeOf(early)));

        Assert.Equal(200, StatusOf(await h.AcceptOfferAsync(enquiry.Id, DataOf<TripOfferResultDto>(offer).Offer.Id, seeker)));

        var notSet = await ShareAsync(h, enquiry.Id, host);
        Assert.Equal((400, StayShareRules.NotSetError), (StatusOf(notSet), ErrorTypeOf(notSet)));

        var guest = await ShareAsync(h, enquiry.Id, seeker);
        Assert.Equal(403, StatusOf(guest));

        await using (var w = Db.CreateContext())
            await w.StayListings.Where(x => x.Id == stay).ExecuteUpdateAsync(x => x.SetProperty(y => y.UpiId, "lakeview@okhdfc"));
        Assert.Equal(200, StatusOf(await ShareAsync(h, enquiry.Id, host)));
        Assert.Equal(200, StatusOf(await ShareAsync(h, enquiry.Id, host)));

        await using var check = Db.CreateContext();
        Assert.Equal(1, await check.TripPushLogs.CountAsync(p => p.DedupeKey == TripOwnerHandlers.ShareKey(enquiry.Id)));
    }
}

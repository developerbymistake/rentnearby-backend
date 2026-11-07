using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// T2: a family books a stay by phone and records it: booked is done on the traveller's word, no host reply needed.
public class T2FamilyPhoneBookingJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task PhoneBooking_RowBooked_EditShorter_Cancel_BackToTodo()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var created = await h.PhoneBookAsync(tripId, seeker, new CreatePhoneBookingRequest
        {
            StayId = stay, Day = 1, AllNights = true, HostName = "Ramesh", RoomName = "Family room", AgreedTotal = 4000,
        });
        Assert.Equal(201, StatusOf(created));
        var enquiryId = DataOf<TripPhoneBookingResultDto>(created).Enquiry.Id;

        var progress = await h.ProgressAsync(tripId);
        var stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Booked, stayRow.State);
        Assert.False(stayRow.Partial);
        Assert.Equal(1, progress.Done);
        Assert.Equal(4, progress.Total);
        Assert.Equal(TripStatuses.Planned, (await h.LoadAsync(tripId)).Trip.Status);

        // One night of two: still the traveller's booking, but the row is not done until every night is covered.
        var shorter = await h.EditPhoneBookingAsync(enquiryId, seeker, new EditPhoneBookingRequest { Nights = 1, AgreedTotal = 2000 });
        Assert.Equal(200, StatusOf(shorter));
        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Booked, stayRow.State);
        Assert.Equal(2, stayRow.NightsTotal);
        Assert.Equal(1, stayRow.NightsCovered);
        Assert.True(stayRow.Partial);
        Assert.Equal(0, (await h.ProgressAsync(tripId)).Done);

        var longer = await h.EditPhoneBookingAsync(enquiryId, seeker, new EditPhoneBookingRequest { AllNights = true, AgreedTotal = 4000 });
        Assert.Equal(200, StatusOf(longer));
        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Booked, stayRow.State);
        Assert.False(stayRow.Partial);
        Assert.Equal(1, (await h.ProgressAsync(tripId)).Done);

        var cancel = await h.CancelAsync(enquiryId, seeker);
        Assert.Equal(200, StatusOf(cancel));
        stayRow = RowOf(await h.RowsAsync(tripId), "stay:1");
        Assert.Equal(TripRowStates.Todo, stayRow.State);
        Assert.Equal(0, (await h.ProgressAsync(tripId)).Done);
    }
}

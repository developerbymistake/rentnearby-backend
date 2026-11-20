using System.Text.Json;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Buddy.Be2;

public class TripViewerProjectionTests
{
    private static readonly Guid TripId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly DateOnly Start = new(2026, 5, 16);

    private static TripPlan Plan()
    {
        var plan = new TripPlan
        {
            CityKey = 210000101, CityName = "Nainital",
            Settings = new TripSettings { StartDate = Start, Adults = 2, Kids = 1 },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 210000101, CityName = "Nainital", Latitude = 29.39, Longitude = 79.45 },
                new TripCityLeg { Index = 2, CityKey = 210000102, CityName = "Rishikesh", Latitude = 30.08, Longitude = 78.26 },
            ],
            Days =
            [
                new TripDay { Day = 1, Leg = 1, Spots = [Spot("sp-1", "Naini Lake", 90), Spot("sp-2", "Naina Devi Temple", 45)] },
                new TripDay { Day = 2, Leg = 1, Spots = [Spot("sp-3", "Snow View Point", 90)] },
                new TripDay { Day = 3, Leg = 2, RestDay = true, Spots = [] },
            ],
        };
        plan.Normalize(210000101, "Nainital", 29.39, 79.45);
        return plan;
    }

    private static TripSpot Spot(string id, string name, int minutes) => new() { Id = id, Name = name, Latitude = 29.3, Longitude = 79.4, Category = "lake", VisitMinutes = minutes };

    private static Trip Trip(TripPlan plan) => new()
    {
        Id = TripId, OwnerUserId = Owner, Title = "Uttarakhand Hills Trip", Status = TripStatuses.Planned, CityKey = 210000101,
        CityName = "Nainital", CityLatitude = 29.39, CityLongitude = 79.45, PlanJson = JsonSerializer.Serialize(plan, TripJson.Options),
        DayCount = 3, SpotCount = 3, StartDate = Start, EndDate = Start.AddDays(2), InviteCode = Guid.Parse("99999999-9999-4999-8999-999999999999"),
        MutedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = new DateTime(2026, 5, 16, 5, 12, 0, DateTimeKind.Utc),
    };

    private static TripEnquiry Stay(string status, string name = "Snow View Hotel") => new()
    {
        Id = Guid.NewGuid(), TripId = TripId, TravellerUserId = Owner, OwnerUserId = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.Parse("77777777-0000-4000-8000-000000000001"), ListingName = name, Day = 1,
        CheckIn = Start, CheckOut = Start.AddDays(2), Status = status, AgreedTotal = 8123, ConversationId = Guid.Parse("88888888-0000-4000-8000-000000000001"),
        ContextJson = "{\"phone\":\"9876543210\"}", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static TripViewerDto Build(IReadOnlyList<TripEnquiry> enquiries, TripTaxiRequestSummary? taxi = null, TripMoneyCard? money = null)
    {
        var plan = Plan();
        return TripViewerProjection.Build(new TripViewerSource(
            Trip(plan), plan, TripSharingRoles.Viewer, "Rahul", "Bakhli#48291736", 2,
            [new TravellerChipDto { UserId = Owner, Initial = "R", Name = "Rahul" }], enquiries, taxi, money, Start.AddDays(1)));
    }

    [Fact]
    public void Allow_list_never_leaks_contact_price_chat_listing_or_plan_internals()
    {
        var taxi = new TripTaxiRequestSummary(Guid.Parse("66666666-0000-4000-8000-000000000001"), RequestStates.Booked, "booked", 3,
            new TripTaxiBookingSummary(Guid.Parse("55555555-0000-4000-8000-000000000001"), BookingStates.Booked, Start, Start.AddDays(2),
                "Suresh Kumar", "Dzire", 4, "Haldwani"));
        var json = JsonSerializer.Serialize(Build([Stay(TripEnquiryStatuses.ConfirmedByBoth)], taxi), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string[] forbidden =
        [
            "phone", "whatsapp", "agreedTotal", "8123", "price", "fare", "conversationId", "88888888", "listingId", "77777777",
            "inviteCode", "99999999", "planLock", "mutedAt", "adults", "kids", "planJson", "guide", "taxiRequest", "66666666",
            "55555555", "Kumar", "upi", "offer", "plate", "steps", "mark", "Haldwani",
        ];
        foreach (var f in forbidden) Assert.DoesNotContain(f, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dto_type_has_only_the_allow_list_fields()
    {
        string[] expected =
        [
            "TripId", "Title", "Status", "Role", "Owner", "LegCities", "DayCount", "SpotCount", "StartDate", "EndDate", "TravellerCount",
            "FirstTravellers", "Legs", "Days", "Stay", "Taxi", "ProgressPercent", "Expenses", "UpdatedAt",
        ];
        Assert.Equal(expected.OrderBy(x => x), typeof(TripViewerDto).GetProperties().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal(["Name", "Username"], typeof(TripOwnerRefDto).GetProperties().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal(["DriverFirstName", "PickupDate", "PickupTime", "State", "VehicleType"], typeof(TripViewerTaxiDto).GetProperties().Select(p => p.Name).OrderBy(x => x));
    }

    [Fact]
    public void Booked_stay_and_taxi_are_shown_with_snapshot_names_only()
    {
        var taxi = new TripTaxiRequestSummary(Guid.NewGuid(), RequestStates.Booked, "booked", 1,
            new TripTaxiBookingSummary(Guid.NewGuid(), BookingStates.Booked, Start, Start.AddDays(2), "Suresh Kumar", "Dzire", 4, "Haldwani"));
        var view = Build([Stay(TripEnquiryStatuses.ConfirmedByBoth), Stay(TripEnquiryStatuses.Asked, "Other Inn")], taxi);
        Assert.Equal(TripPartWords.Booked, view.Stay.State);
        var item = Assert.Single(view.Stay.Items);
        Assert.Equal("Snow View Hotel", item.StayName);
        Assert.Equal(2, item.Nights);
        Assert.Equal("Nainital", item.CityName);
        Assert.Equal(TripPartWords.Booked, view.Taxi.State);
        Assert.Equal("Dzire", view.Taxi.VehicleType);
        Assert.Equal("Suresh", view.Taxi.DriverFirstName);
        Assert.Equal(Start, view.Taxi.PickupDate);
    }

    [Fact]
    public void A_stay_booked_by_the_traveller_reads_booked_like_the_owners_card()
    {
        var view = Build([Stay(TripEnquiryStatuses.BookedByTraveller)]);
        Assert.Equal(TripPartWords.Booked, view.Stay.State);
        Assert.Equal("Snow View Hotel", Assert.Single(view.Stay.Items).StayName);
    }

    [Fact]
    public void Nothing_booked_reads_not_added_and_an_open_ask_reads_arranging()
    {
        var empty = Build([]);
        Assert.Equal(TripPartWords.NotAdded, empty.Stay.State);
        Assert.Empty(empty.Stay.Items);
        Assert.Equal(TripPartWords.NotAdded, empty.Taxi.State);
        Assert.Null(empty.Taxi.VehicleType);
        Assert.Equal(TripPartWords.Arranging, Build([Stay(TripEnquiryStatuses.Asked)]).Stay.State);
        var active = new TripTaxiRequestSummary(Guid.NewGuid(), RequestStates.Collecting, "collecting", 1);
        Assert.Equal(TripPartWords.Arranging, Build([], active).Taxi.State);
        var done = new TripTaxiRequestSummary(Guid.NewGuid(), RequestStates.Completed, "completed", 1,
            new TripTaxiBookingSummary(Guid.NewGuid(), BookingStates.Completed, Start, Start, "Suresh", "Dzire", 4, "X"));
        Assert.Equal(TripPartWords.Completed, Build([], done).Taxi.State);
    }

    [Fact]
    public void Legs_and_days_follow_the_plan()
    {
        var view = Build([]);
        Assert.Equal(["Nainital", "Rishikesh"], view.LegCities);
        Assert.Equal(2, view.Legs.Count);
        Assert.Equal((1, 2, 2, 3), (view.Legs[0].FirstDay, view.Legs[0].LastDay, view.Legs[0].Nights, view.Legs[0].SpotCount));
        Assert.Equal((3, 3, 1, 0), (view.Legs[1].FirstDay, view.Legs[1].LastDay, view.Legs[1].Nights, view.Legs[1].SpotCount));
        Assert.Equal(3, view.Days.Count);
        Assert.Equal(135, view.Days[0].TotalMinutes);
        Assert.Equal(2, view.Days[0].PlaceCount);
        Assert.Equal(Start, view.Days[0].Date);
        Assert.True(view.Days[2].RestDay);
        Assert.Equal(2, view.Days[2].Leg);
        Assert.Equal("Rahul", view.Owner.Name);
        Assert.Equal("Bakhli#48291736", view.Owner.Username);
    }

    [Fact]
    public void Money_card_defaults_to_settled_and_carries_day_today()
    {
        var none = Build([]);
        Assert.False(none.Expenses.HasExpenses);
        Assert.True(none.Expenses.Settled);
        Assert.Equal(2, none.Expenses.DayToday);
        var card = Build([], money: new TripMoneyCard(TripId, true, 9000, 2, 4500, 1, false)).Expenses;
        Assert.Equal((true, 9000L, 4500, 1, false), (card.HasExpenses, card.Spent, card.MyShare, card.OpenPayments, card.Settled));
    }

    [Theory]
    [InlineData(TripPartWords.Booked, "booked")]
    [InlineData(TripPartWords.Completed, "booked")]
    [InlineData(TripPartWords.Arranging, "arranging")]
    [InlineData(TripPartWords.NotAdded, "notAdded")]
    public void Card_words(string state, string expected) => Assert.Equal(expected, TripPartWords.Card(state));
}

using System.Text.Json;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-SK-3 / FB-06: a merged (still Asked) stay enquiry carries the listed total and breakdown of every asked night.
public class ListedTotalJourneyTests
{
    private static readonly DateOnly CheckIn = new(2026, 12, 10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<ListedRoom> Rooms = [new("Deluxe", 2500, 2, null)];

    [Fact]
    public void One_night_then_two_adjacent_nights_cover_three_nights()
    {
        var row = new TripEnquiry { Kind = TripEnquiryKinds.Stay, CheckIn = CheckIn, CheckOut = CheckIn.AddDays(1) };
        StayListedQuote.ApplyMerged(row, Rooms, 2, CheckIn, CheckIn.AddDays(1), [], Json);
        Assert.Equal(2500, row.ListedStayTotal);

        StayListedQuote.ApplyMerged(row, Rooms, 2, CheckIn, CheckIn.AddDays(3), [], Json);
        Assert.Equal(7500, row.ListedStayTotal);
        var lines = JsonSerializer.Deserialize<List<ListedBreakdownLineDto>>(row.ListedStayBreakdownJson!, Json)!;
        Assert.Equal(3, lines.Sum(l => l.Nights));
        Assert.Equal(2500, row.ListedStayTotal / 3);
    }

    [Fact]
    public void A_festival_rule_inside_the_merged_nights_is_priced_once_per_night()
    {
        var rule = new LabelledRule(new PriceRule(CheckIn.AddDays(2), CheckIn.AddDays(2), false, 20), "Diwali");
        var row = new TripEnquiry { Kind = TripEnquiryKinds.Stay };
        StayListedQuote.ApplyMerged(row, Rooms, 2, CheckIn, CheckIn.AddDays(3), [rule], Json);
        Assert.Equal(2500 + 2500 + 3000, row.ListedStayTotal);
    }

    [Fact]
    public void No_priced_room_keeps_the_stored_values()
    {
        var row = new TripEnquiry { Kind = TripEnquiryKinds.Stay, ListedStayTotal = 2500, ListedStayBreakdownJson = "[]" };
        StayListedQuote.ApplyMerged(row, [], 2, CheckIn, CheckIn.AddDays(3), [], Json);
        Assert.Equal(2500, row.ListedStayTotal);
    }

    [Fact]
    public void Create_handler_recomputes_in_the_merge_loop()
    {
        var src = File.ReadAllText(Path.Combine(Trips.CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs"));
        Assert.Contains("StayListedQuote.ApplyMerged(row, mergeFacts.Rooms, partyGuests, mergedIn, mergedOut", src);
    }
}

using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Trips;

public class TripEnquiryDtoLegFieldsTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    private static TripPlan TwoLegs() => new()
    {
        CityKey = 210000123,
        CityName = "Haridwar",
        Legs =
        [
            new TripCityLeg { Index = 1, CityKey = 210000123, CityName = "Haridwar", Latitude = 29.95, Longitude = 78.16 },
            new TripCityLeg { Index = 2, CityKey = 210000456, CityName = "Badrinath", Latitude = 30.74, Longitude = 79.49 },
        ],
        Days =
        [
            new TripDay { Day = 1, Leg = 1 },
            new TripDay { Day = 2, Leg = 2 },
            new TripDay { Day = 3, Leg = 2 },
            new TripDay { Day = 4, Leg = 2 },
        ],
    };

    private static TripEnquiry HaridwarStayOnNight2() => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.NewGuid(),
        ListingName = "Ganga View",
        ListingLatitude = 29.95,
        ListingLongitude = 78.16,
        Day = 2,
        CheckIn = Start.AddDays(1),
        CheckOut = Start.AddDays(2),
        ContextJson = JsonSerializer.Serialize(new TripEnquiryContext { City = "Haridwar" }, TripJson.Options),
        Status = TripEnquiryStatuses.ConfirmedByBoth,
    };

    [Fact]
    public void Traveller_viewer_gets_wrong_city_for_a_haridwar_room_on_a_badrinath_night()
    {
        var e = HaridwarStayOnNight2();
        var dto = new TripEnquiryDto();

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), Start, travellerViewer: true);

        Assert.True(dto.WrongCity);
        Assert.Equal(1, dto.WrongNights);
        Assert.Equal(1, dto.WrongTotal);
        Assert.Equal(2, dto.WrongFirstDay);
        Assert.Equal("Badrinath", dto.WrongDayCity);
        Assert.False(dto.OutsideTrip);
    }

    [Fact]
    public void Owner_viewer_never_learns_the_other_cities()
    {
        var e = HaridwarStayOnNight2();
        var dto = new TripEnquiryDto { WrongCity = true, WrongNights = 3, OutsideTrip = true };

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), Start, travellerViewer: false);

        Assert.False(dto.WrongCity);
        Assert.Equal(0, dto.WrongNights);
        Assert.Equal(0, dto.WrongTotal);
        Assert.Null(dto.WrongFirstDay);
        Assert.Null(dto.WrongDayCity);
        Assert.False(dto.OutsideTrip);
    }

    [Fact]
    public void Segment_row_is_never_flagged()
    {
        var e = HaridwarStayOnNight2();
        e.Kind = TripEnquiryKinds.Taxi;
        e.DaysJson = "[2]";
        e.ContextJson = JsonSerializer.Serialize(
            new TripEnquiryContext { City = "Haridwar", JourneyId = Guid.NewGuid(), LegIndex = 1 }, TripJson.Options);
        Assert.True(TripSegmentRows.IsSegment(e));
        var dto = new TripEnquiryDto();

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), Start, travellerViewer: true);

        Assert.False(dto.WrongCity);
        Assert.False(dto.OutsideTrip);
    }

    [Fact]
    public void Same_city_row_is_not_flagged()
    {
        var e = HaridwarStayOnNight2();
        e.ContextJson = JsonSerializer.Serialize(new TripEnquiryContext { City = "badrinath" }, TripJson.Options);
        var dto = new TripEnquiryDto();

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), Start, travellerViewer: true);

        Assert.False(dto.WrongCity);
        Assert.Equal(0, dto.WrongNights);
    }

    [Fact]
    public void Row_before_a_moved_start_is_outside_the_trip()
    {
        var e = HaridwarStayOnNight2();
        var dto = new TripEnquiryDto();

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), Start.AddDays(5), travellerViewer: true);

        Assert.True(dto.OutsideTrip);
        Assert.False(dto.WrongCity);
    }

    [Fact]
    public void Without_a_start_date_nothing_is_flagged()
    {
        var e = HaridwarStayOnNight2();
        var dto = new TripEnquiryDto();

        TripEnquiryDtoBuilder.ApplyLegFlags(dto, e, TwoLegs(), null, travellerViewer: true);

        Assert.False(dto.WrongCity);
        Assert.False(dto.OutsideTrip);
    }
}

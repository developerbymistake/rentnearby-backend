using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

// Per-day planner points (last night's stay to tonight's), Best order, own step travel and the per-day taxi pickup.
public class TripDayAnchorsTests
{
    private const int Limit = 450;

    private static TripSpot Spot(string id) => new()
    {
        Id = id, Name = id, Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60,
    };

    // a, b, c = 0..2, stay A = 3, stay B = 4. a-b 10, b-c 10, a-c 30; A next to a (5), B next to c (5), else 40.
    private static RoadMatrix Matrix()
    {
        var m = new int[5, 5];
        void Set(int i, int j, int v) { m[i, j] = v; m[j, i] = v; }
        Set(0, 1, 10); Set(1, 2, 10); Set(0, 2, 30);
        Set(3, 0, 5); Set(3, 1, 40); Set(3, 2, 40);
        Set(4, 2, 5); Set(4, 0, 40); Set(4, 1, 40);
        Set(3, 4, 45);
        return new RoadMatrix(m, new double[5, 5], new bool[5, 5]);
    }

    [Fact]
    public void Recalculate_DayRunsFromLastNightToTonight()
    {
        var spots = new List<TripSpot> { Spot("a"), Spot("b"), Spot("c") };
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, spots.ToList()) };

        var r = DaySplitter.Recalculate(days, Limit, null, Matrix(), spots, 0, null, [new DayEnds(3, 4)]);

        Assert.Equal(new[] { "anchor>a", "a>b", "b>c", "c>anchor" }, r.Days[0].Legs.Select(l => $"{l.FromId}>{l.ToId}").ToArray());
        Assert.Equal(30, r.Days[0].DriveMinutes);
    }

    [Fact]
    public void Recalculate_OpenEnds_NoBaseLegs()
    {
        var spots = new List<TripSpot> { Spot("a"), Spot("b") };
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, spots.ToList()) };

        var r = DaySplitter.Recalculate(days, Limit, null, Matrix(), spots, 0, null, [new DayEnds(-1, -1)]);

        Assert.Equal(10, r.Days[0].DriveMinutes);
    }

    [Fact]
    public void BestOrder_ReordersForStartAndEnd()
    {
        var spots = new List<TripSpot> { Spot("a"), Spot("b"), Spot("c") };
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, new List<TripSpot> { spots[2], spots[0], spots[1] }) };

        var keep = DaySplitter.Recalculate(days, Limit, null, Matrix(), spots, 0, null, [new DayEnds(3, 4)]);
        var best = DaySplitter.Recalculate(days, Limit, null, Matrix(), spots, 0, null, [new DayEnds(3, 4, BestOrder: true)]);

        Assert.Equal(new[] { "c", "a", "b" }, keep.Days[0].Spots.Select(s => s.Id).ToArray());
        Assert.Equal(new[] { "a", "b", "c" }, best.Days[0].Spots.Select(s => s.Id).ToArray());
        Assert.True(best.Days[0].DriveMinutes < keep.Days[0].DriveMinutes);
    }

    [Fact]
    public void Between_KeepsGivenOrderOnATie()
    {
        var m = new int[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            if (i != j) m[i, j] = 10;

        Assert.Equal(new[] { 2, 0, 1 }, RoutePath.Between(m, [2, 0, 1], -1, -1));
    }

    [Fact]
    public void Between_GreedyAboveExactLimit_NeverWorse()
    {
        var n = RoutePath.ExactBetweenMax + 2;
        var m = new int[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            m[i, j] = Math.Abs(i - j) * 10;
        var given = Enumerable.Range(0, n).Reverse().ToArray();

        var order = RoutePath.Between(m, given, 0, -1);

        Assert.Equal(Enumerable.Range(0, n).ToArray(), order);
    }

    [Fact]
    public void PlanValidator_DayPoints_BothOrNeither()
    {
        var v = new PlanTripRequestValidator();
        PlanTripRequest Req(double? lat, double? lon) => new()
        {
            CityKey = 502141,
            Mode = TripPlanModes.Recalculate,
            Days =
            [
                new PlanDayInput
                {
                    Day = 1,
                    Spots = [new PlanSpotInput { Id = "a", Name = "a", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60 }],
                    StartLatitude = lat,
                    StartLongitude = lon,
                    BestOrder = true,
                },
            ],
        };

        Assert.True(v.Validate(Req(29.38, 79.46)).IsValid);
        Assert.True(v.Validate(Req(null, null)).IsValid);
        ValidationAssert.HasMessage(v.Validate(Req(29.38, null)), PlanTripRequestValidator.InvalidCoordinatesMessage);
        ValidationAssert.HasMessage(v.Validate(Req(95, 79.46)), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    private static SaveTripRequest SaveWith(TripStep step) => new()
    {
        Plan = new TripPlan
        {
            CityKey = 502141,
            Days = [new TripDay { Day = 1, Spots = [Spot("a")] }],
            Steps = new TripSteps { GettingThere = step },
        },
    };

    [Fact]
    public void SaveValidator_OwnStepTravel()
    {
        var v = new SaveTripRequestValidator();

        Assert.True(v.Validate(SaveWith(new TripStep
        {
            Status = TripStepStatuses.Own, How = TripTravelHows.All[0], OwnName = "Volvo bus", OwnPhone = "9876543210", LeavesAt = "06:30",
        })).IsValid);
        Assert.False(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Chosen, How = TripTravelHows.All[0] })).IsValid);
        Assert.False(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Own, How = "plane" })).IsValid);
        Assert.False(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Own, LeavesAt = "25:00" })).IsValid);
        Assert.False(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Own, OwnPhone = "12345" })).IsValid);
    }

    [Fact]
    public void Merge_OlderApp_KeepsOwnStepTravel()
    {
        var stored = new TripPlan
        {
            Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own, How = "bus", OwnName = "Volvo", LeavesAt = "06:30" } },
        };
        var plan = new TripPlan { Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own } } };

        TripPlanMerge.KeepStored(plan, stored, 4);

        Assert.Equal("bus", plan.Steps!.GettingThere!.How);
        Assert.Equal("Volvo", plan.Steps.GettingThere.OwnName);
        Assert.Equal("06:30", plan.Steps.GettingThere.LeavesAt);
    }

    private static TripPlan TwoNights(string? night1 = "Deodar Homestay", bool skipped = false) => new()
    {
        Days =
        [
            new TripDay { Day = 1, Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = night1 ?? "", Skipped = skipped ? true : null } },
            new TripDay { Day = 2 },
        ],
    };

    [Fact]
    public void DayStartPlace_IsLastNightsStay()
    {
        Assert.Null(TripDayStart.PlaceOf(TwoNights(), 1));
        Assert.Equal("Deodar Homestay", TripDayStart.PlaceOf(TwoNights(), 2));
        Assert.Null(TripDayStart.PlaceOf(TwoNights(skipped: true), 2));
        Assert.Null(TripDayStart.PlaceOf(TwoNights(""), 2));
    }

    [Theory]
    [InlineData(null, "Deodar", "Hill View", "Hill View")]
    [InlineData("deodar", "Deodar", "Hill View", "Hill View")]
    [InlineData("Mall Road gate", "Deodar", "Hill View", "Mall Road gate")]
    [InlineData("Deodar", null, "Hill View", "Deodar")]
    [InlineData("Deodar", "Deodar", null, null)]
    public void Pickup_FollowsTheStartUnlessTyped(string? current, string? oldStart, string? newStart, string? expected)
        => Assert.Equal(expected, TripDayStart.Pickup(current, oldStart, newStart));
}

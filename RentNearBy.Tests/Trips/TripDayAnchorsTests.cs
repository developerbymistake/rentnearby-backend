using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

// Per-day planner points (last night's stay to tonight's), Best order and the own step status.
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
            Legs = [new TripCityLeg { Index = 1, CityKey = 502141, CityName = "Nainital", Latitude = 29.38, Longitude = 79.45 }],
            Days = [new TripDay { Day = 1, Spots = [Spot("a")] }],
            Steps = new TripSteps { GettingThere = step },
        },
    };

    [Fact]
    public void SaveValidator_OwnStep_IsOnlyAStatus()
    {
        var v = new SaveTripRequestValidator();

        Assert.True(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Own })).IsValid);
        Assert.True(v.Validate(SaveWith(new TripStep { Status = TripStepStatuses.Skipped })).IsValid);
        Assert.False(v.Validate(SaveWith(new TripStep { Status = "notNeeded" })).IsValid);
    }
}

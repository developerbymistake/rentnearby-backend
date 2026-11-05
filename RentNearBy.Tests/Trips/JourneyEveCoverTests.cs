namespace RentNearBy.Tests.Trips;

public class JourneyEveCoverTests
{
    private static readonly DateOnly Travel = new(2026, 11, 12);
    private static readonly DateTime At1800Ist = new(2026, 11, 11, 12, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime At1900Ist = new(2026, 11, 11, 13, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime At2000Ist = new(2026, 11, 11, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void FallbackIsTwoHoursAfterTravelEveStart()
        => Assert.Equal(At2000Ist, JourneyEveCover.FallbackUtc(Travel));

    [Fact]
    public void KillSwitchOff_JourneySendsItsOwnEve()
        => Assert.False(JourneyEveCover.Covered(false, tripExecutionEnabled: false, tripEveCovers: true, Travel, At1800Ist));

    [Fact]
    public void TimerOn_QualifyingTrip_CoveredInsideGrace()
        => Assert.True(JourneyEveCover.Covered(false, true, true, Travel, At1900Ist));

    [Fact]
    public void TimerOn_NoTripPushByFallback_JourneySends()
    {
        Assert.False(JourneyEveCover.Covered(false, true, true, Travel, At2000Ist));
        Assert.False(JourneyEveCover.Covered(false, true, true, Travel, At2000Ist.AddHours(3)));
    }

    [Fact]
    public void TripPushLogged_AlwaysCovered()
    {
        Assert.True(JourneyEveCover.Covered(true, false, false, Travel, At2000Ist.AddHours(1)));
        Assert.True(JourneyEveCover.Covered(true, true, true, Travel, At1800Ist));
    }

    [Fact]
    public void NonQualifyingTrip_NeverCoveredWithoutLog()
        => Assert.False(JourneyEveCover.Covered(false, true, false, Travel, At1800Ist));
}

namespace RentNearBy.Tests.PlayBilling;

public class PlayStatesTests
{
    public enum Expiry { None, Future, Past }

    private static DateTime? At(Expiry e) => e switch
    {
        Expiry.Future => Dates.Far,
        Expiry.Past => Dates.Past,
        _ => null,
    };

    [Theory]
    [InlineData(PlayStates.Active, Expiry.None, true)]
    [InlineData(PlayStates.Active, Expiry.Past, true)]
    [InlineData(PlayStates.InGracePeriod, Expiry.None, true)]
    [InlineData(PlayStates.Canceled, Expiry.Future, true)]
    [InlineData(PlayStates.Canceled, Expiry.Past, false)]
    [InlineData(PlayStates.Canceled, Expiry.None, false)]
    [InlineData(PlayStates.Expired, Expiry.Future, false)]
    [InlineData(PlayStates.OnHold, Expiry.Future, false)]
    [InlineData(PlayStates.Paused, Expiry.Future, false)]
    [InlineData(PlayStates.Pending, Expiry.Future, false)]
    [InlineData(PlayStates.PendingPurchaseCanceled, Expiry.Future, false)]
    [InlineData(PlayStates.Unspecified, Expiry.Future, false)]
    public void IsEntitled_StateAndExpiry(string state, Expiry expiry, bool expected)
        => Assert.Equal(expected, PlayStates.IsEntitled(state, At(expiry)));
}

namespace RentNearBy.Tests.Plans;

public class PlayReplacementPolicyTests
{
    private const long Rs = 1_000_000L;

    [Fact]
    public void GooglePriceCut_CheaperTarget_IsDeferred()
        => Assert.Equal(PlayReplacementModes.Deferred,
            PlayReplacementPolicy.Decide(false, 1, 1, PlayReplacementPolicy.PriceMicros(149 * Rs, 149), PlayReplacementPolicy.PriceMicros(129 * Rs, 199)));

    [Fact]
    public void MirrorOnly_SameCaseLooksLikeUpgrade()
        => Assert.Equal(PlayReplacementModes.ChargeFullPrice,
            PlayReplacementPolicy.Decide(false, 1, 1, PlayReplacementPolicy.PriceMicros(null, 149), PlayReplacementPolicy.PriceMicros(null, 199)));

    [Theory]
    [InlineData(3, 897, 1, 299, "chargeFullPrice")]
    [InlineData(1, 299, 12, 3588, "chargeFullPrice")]
    [InlineData(1, 299, 12, 3587, "deferred")]
    [InlineData(1, 199, 1, 299, "chargeFullPrice")]
    public void OtherProduct_ExactPerMonthCompare_TieIsUpgrade(int oldCycle, int oldInr, int newCycle, int newInr, string expected)
        => Assert.Equal(expected, PlayReplacementPolicy.Decide(false, oldCycle, newCycle, oldInr * Rs, newInr * Rs));

    [Fact]
    public void FractionalPaise_NoDoubleRounding()
        => Assert.Equal(PlayReplacementModes.Deferred,
            PlayReplacementPolicy.Decide(false, 3, 1, 449_500_000L, 149_830_000L));

    [Theory]
    [InlineData(1, 3, "chargeFullPrice")]
    [InlineData(3, 1, "withoutProration")]
    [InlineData(1, 1, "withoutProration")]
    public void SameProduct_OnlyLongerCycleChargesNow(int fromCycle, int toCycle, string expected)
        => Assert.Equal(expected, PlayReplacementPolicy.Decide(true, fromCycle, toCycle, 999 * Rs, 1 * Rs));

    [Fact]
    public void PriceMicros_ZeroGoogle_FallsBackToMirror()
        => Assert.Equal(149 * Rs, PlayReplacementPolicy.PriceMicros(0, 149));
}

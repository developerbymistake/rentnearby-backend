namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.1 / 3.2: ranking keys while payment is OFF are the default plan's for everyone (nobody ranked above anybody), a grant
// keeps its own; expiry (no active row) writes 0 / false; Plus with payment ON writes 1 / true. SetRankAsync and the daily
// reconcile write what Compute returns.
public class RankingKeysTests
{
    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    [PlanKeyTest(PlanFeatureKeys.Priority)]
    public void Priority_ZeroForAllWhileOff_GrantKeepsOwn(string kind)
    {
        Assert.Equal(0, Be3Kit.Off(kind).PriorityRank);
        Assert.Equal(1, Be3Kit.OffWithGrant(kind).PriorityRank);
        Assert.Equal(0, Be3Kit.Free(kind).PriorityRank);
        Assert.Equal(1, Be3Kit.Plus(kind).PriorityRank);
        Assert.Contains("PlanFeatureValues.OffValues(snapshot, kind)", Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlanSlotService.cs"));
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    [PlanKeyTest(PlanFeatureKeys.Featured)]
    public void Featured_OffForAllWhileOff_GrantKeepsOwn(string kind)
    {
        Assert.False(Be3Kit.Off(kind).IsFeatured);
        Assert.True(Be3Kit.OffWithGrant(kind).IsFeatured);
        Assert.False(Be3Kit.Free(kind).IsFeatured);
        Assert.True(Be3Kit.Plus(kind).IsFeatured);
    }

    [Fact]
    public void Expiry_and_grant_write_ranks_from_the_entitlement()
    {
        var slots = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlanSlotService.cs");
        var expire = slots[slots.IndexOf("public async Task<ExpireEffectsResult> ExpireEffectsAsync", StringComparison.Ordinal)..];
        Assert.Contains("SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured, limits.RingWeight)", expire);
        Assert.Contains("planRows.ApplyForOwnerAsync(userId, featureKey, limits)", expire);
    }
}

using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 7.1, 3.2 RingWeight: PlanSlotService is the only writer of TaxiListings.RingWeight; Free, ended and payment OFF = 1;
// a grant while OFF keeps its own value; the fair queue reads the column through the pool query.
public class RingWeightTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.RingWeight)]
    public void RingWeight_FreeAndOffWriteOne_PlusAndGrantWriteTheirOwn()
    {
        Assert.Equal(1, Be3Kit.Free(ListingKinds.Taxi).RingWeight);
        Assert.Equal(2, Be3Kit.Plus(ListingKinds.Taxi).RingWeight);
        Assert.Equal(1, Be3Kit.Off(ListingKinds.Taxi).RingWeight);
        Assert.Equal(2, Be3Kit.OffWithGrant(ListingKinds.Taxi).RingWeight);

        var slots = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlanSlotService.cs");
        Assert.Contains("\"RingWeight\" = {\"{3}\"}", slots);
        Assert.Contains("await SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured, limits.RingWeight)", slots);
        Assert.Contains(", \\\"RingWeight\\\" = target.\\\"Weight\\\"", slots);
    }

    [Fact]
    public void Pool_query_reads_the_column_and_the_matcher_passes_it_to_the_queue()
    {
        Assert.Contains("l.\"RingWeight\"::int AS \"RingWeight\"", RentNearBy.Infrastructure.Services.TaxiRequests.TaxiCandidateQuery.PoolSql);
        var pass = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "Matching", "TaxiMatcherPass.cs");
        Assert.Contains("pairs : [], [], p.Weight))", pass);
        Assert.Equal(1, TaxiFairQueue.WeightOf(Driver(weight: 0)));
        Assert.Equal(5, TaxiFairQueue.WeightOf(Driver(weight: 9)));
        Assert.Equal(2, TaxiFairQueue.WeightOf(Driver(weight: 2)));
    }

    internal static FairQueueDriver Driver(int weight, Guid? id = null)
        => new(id ?? Guid.NewGuid(), Guid.NewGuid(), 0, 0, null, false, false, 6, [new TaxiRoutePair(1, 2)], [], weight);
}

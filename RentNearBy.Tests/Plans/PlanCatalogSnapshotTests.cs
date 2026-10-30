namespace RentNearBy.Tests.Plans;

public class PlanCatalogSnapshotTests
{
    private static readonly Guid A = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("11111111-0000-0000-0000-000000000003");

    [Fact]
    public void PlansOf_Feature_OrderedBySortOrderThenName()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(
            CatalogPlanBuilder.Plus().WithId(A).WithName("Zeta").WithSortOrder(2).WithPlayProduct(null).Build(),
            CatalogPlanBuilder.Plus().WithId(B).WithName("Beta").WithSortOrder(2).WithPlayProduct(null).Build(),
            CatalogPlanBuilder.Plus().WithId(C).WithName("Omega").WithSortOrder(1).WithPlayProduct(null).Build(),
            PlanCatalogSnapshotBuilder.PlotPlus).Build();

        Assert.Equal(new[] { C, B, A }, snapshot.PlansOf(ListingKinds.Room).Select(p => p.Id));
    }

    [Fact]
    public void PlansOf_FeatureWithoutPlans_IsEmpty()
        => Assert.Empty(new PlanCatalogSnapshotBuilder().Build().PlansOf(ListingKinds.Taxi));

    [Fact]
    public void GetPlan_UnknownId_ReturnsNull()
        => Assert.Null(new PlanCatalogSnapshotBuilder().Build().GetPlan(Guid.NewGuid()));

    [Fact]
    public void GetPlan_KnownId_ReturnsPlan()
        => Assert.Equal("Plus", new PlanCatalogSnapshotBuilder().Build().GetPlan(TestIds.RoomPlus)?.Name);

    [Fact]
    public void FindByPlayProduct_SharedProductId_ReturnsFirstInCatalogOrder()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(
            CatalogPlanBuilder.Plus().WithId(A).WithName("Later").WithSortOrder(5).WithPlayProduct("dup").Build(),
            CatalogPlanBuilder.Plus().WithId(B).WithName("Earlier").WithSortOrder(1).WithPlayProduct("dup").Build()).Build();

        Assert.Equal(B, snapshot.FindByPlayProduct("dup")?.Id);
    }

    [Fact]
    public void FindByPlayProduct_UnknownProduct_ReturnsNull()
        => Assert.Null(new PlanCatalogSnapshotBuilder().Build().FindByPlayProduct("nope"));

    [Fact]
    public void FindByPlayProduct_DisabledPlan_IsStillFound()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(CatalogPlanBuilder.Plus().Disabled().Build()).Build();

        Assert.Equal(TestIds.RoomPlus, snapshot.FindByPlayProduct("room_plus")?.Id);
    }

    [Fact]
    public void FindPrice_KnownBasePlan_ReturnsThatPrice()
    {
        var price = new PlanCatalogSnapshotBuilder().Build().FindPrice(TestIds.RoomPlus, "cycle-3m");

        Assert.NotNull(price);
        Assert.Equal(TestIds.Price3m, price.Id);
        Assert.Equal((short)3, price.CycleMonths);
    }

    [Theory]
    [InlineData("cycle-6m")]
    [InlineData("")]
    public void FindPrice_UnknownBasePlan_ReturnsNull(string basePlanId)
        => Assert.Null(new PlanCatalogSnapshotBuilder().Build().FindPrice(TestIds.RoomPlus, basePlanId));

    [Fact]
    public void FindPrice_UnknownPlan_ReturnsNull()
        => Assert.Null(new PlanCatalogSnapshotBuilder().Build().FindPrice(Guid.NewGuid(), "cycle-1m"));

    [Fact]
    public void DefaultPlan_Seeded_ReturnsFreePlanAndItsValues()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().Build();

        Assert.Equal(TestIds.RoomFree, snapshot.DefaultPlan(ListingKinds.Room)?.Id);
        Assert.Equal(new PlanFeatureValues(1, 0, false, 0), snapshot.DefaultValues(ListingKinds.Room));
    }

    [Fact]
    public void DefaultValues_NoDefaultPlan_ReturnsFallback()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(PlanCatalogSnapshotBuilder.RoomPlus).Build();

        Assert.Null(snapshot.DefaultPlan(ListingKinds.Room));
        Assert.Equal(PlanFeatureValues.Fallback, snapshot.DefaultValues(ListingKinds.Room));
    }

    [Fact]
    public void FeaturesOf_And_PricesOf_UnknownPlan_AreEmpty()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().Build();

        Assert.Empty(snapshot.FeaturesOf(Guid.NewGuid()));
        Assert.Empty(snapshot.PricesOf(Guid.NewGuid()));
    }

    [Fact]
    public void DistinctPriorityRanks_RepeatedPriorities_DistinctHighToLowWithZero()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(
            CatalogPlanBuilder.Plus().WithId(A).WithPriority(2).WithPlayProduct(null).Build(),
            CatalogPlanBuilder.Plus().WithId(B).WithPriority(0).WithPlayProduct(null).Build(),
            CatalogPlanBuilder.Plus().WithId(C).WithPriority(5).WithPlayProduct(null).Build(),
            CatalogPlanBuilder.Plus().WithId(Guid.NewGuid()).WithPriority(5).WithPlayProduct(null).Build()).Build();

        Assert.Equal(new short[] { 5, 2, 0 }, snapshot.DistinctPriorityRanks(ListingKinds.Room));
    }

    [Fact]
    public void DistinctPriorityRanks_FeatureWithoutPlans_IsZeroOnly()
        => Assert.Equal(new short[] { 0 }, new PlanCatalogSnapshotBuilder().Build().DistinctPriorityRanks(ListingKinds.Taxi));

    [Fact]
    public void DistinctPriorityRanks_NegativePriority_ClampsToZero()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(CatalogPlanBuilder.Plus().WithPriority(-3).Build()).Build();

        Assert.Equal(new short[] { 0 }, snapshot.DistinctPriorityRanks(ListingKinds.Room));
    }

    [Fact]
    public void DistinctPriorityRanks_DisabledPlan_RankIncluded()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(
            PlanCatalogSnapshotBuilder.RoomFree,
            CatalogPlanBuilder.Plus().WithPriority(4).Disabled().Build()).Build();

        Assert.Equal(new short[] { 4, 0 }, snapshot.DistinctPriorityRanks(ListingKinds.Room));
    }

    [Fact]
    public void DistinctPriorityRanks_OtherFeature_NotMixedIn()
    {
        var snapshot = new PlanCatalogSnapshotBuilder().With(
            CatalogPlanBuilder.Plus().WithPriority(7).Build(),
            CatalogPlanBuilder.Plus().ForFeature(ListingKinds.Plot).WithId(TestIds.PlotPlus).WithPlayProduct("plot_plus").WithPriority(3).Build()).Build();

        Assert.Equal(new short[] { 3, 0 }, snapshot.DistinctPriorityRanks(ListingKinds.Plot));
    }
}

using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Taxis;

public class TaxiCacheKeyTests
{
    private static readonly Guid District = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid City = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void LocalKey_RateVariant_Differs()
        => Assert.NotEqual(
            TaxiCache.LocalKey(District, City, null, 20),
            TaxiCache.LocalKey(District, City, null, 20, localRate: true));

    [Fact]
    public void LocalKey_RateVariant_EndsWithRate()
        => Assert.EndsWith(":rate", TaxiCache.LocalKey(District, City, null, 20, localRate: true));

    [Fact]
    public void LocalKey_RateVariant_StaysUnderDistrictPrefix()
        => Assert.StartsWith($"taxis:local:{District:N}:", TaxiCache.LocalKey(District, null, "SUV", 20, true, true));

    [Fact]
    public void LocalKey_FourArgs_Unchanged()
        => Assert.Equal(
            $"taxis:local:{District:N}:{City:N}:Sedan:10",
            TaxiCache.LocalKey(District, City, "Sedan", 10));
}

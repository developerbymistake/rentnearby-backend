using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Perf;

public class AdminGeoProjectionTests
{
    public class DistrictRepoStub : DispatchProxy
    {
        public List<DistrictDto> Rows = [];
        public List<string> Calls = [];

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name != nameof(IDistrictRepository.GetAllDtosAsync)) throw new NotSupportedException(m?.Name);
            Calls.Add(m.Name);
            return Task.FromResult(Rows.ToList());
        }
    }

    public class CityRepoStub : DispatchProxy
    {
        public List<CityDto> Rows = [];
        public List<string> Calls = [];
        public Guid? SeenDistrict;

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            switch (m?.Name)
            {
                case nameof(ICityRepository.GetDtosByDistrictIdAsync):
                    Calls.Add(m.Name);
                    SeenDistrict = (Guid)a![0]!;
                    return Task.FromResult(Rows.ToList());
                case nameof(ICityRepository.GetAllDtosAsync):
                    Calls.Add(m.Name);
                    return Task.FromResult(Rows.ToList());
                default:
                    throw new NotSupportedException(m?.Name);
            }
        }
    }

    public class UowStub : DispatchProxy
    {
        public IDistrictRepository? Districts;
        public ICityRepository? Cities;

        protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
        {
            "get_Districts" => Districts,
            "get_Cities" => Cities,
            _ => throw new NotSupportedException(m?.Name),
        };
    }

    private static (IUnitOfWork Uow, DistrictRepoStub D, CityRepoStub C) Uow()
    {
        var d = DispatchProxy.Create<IDistrictRepository, DistrictRepoStub>();
        var c = DispatchProxy.Create<ICityRepository, CityRepoStub>();
        var uow = DispatchProxy.Create<IUnitOfWork, UowStub>();
        ((UowStub)(object)uow).Districts = d;
        ((UowStub)(object)uow).Cities = c;
        return (uow, (DistrictRepoStub)(object)d, (CityRepoStub)(object)c);
    }

    private static readonly List<DistrictDto> Districts =
    [
        new() { Id = Guid.NewGuid(), Name = "Nainital", StateName = "Uttarakhand", IsActive = true, BusinessOnboardingEnabled = true },
        new() { Id = Guid.NewGuid(), Name = "Almora", StateName = "Uttarakhand", IsActive = false, BusinessOnboardingEnabled = false },
        new() { Id = Guid.NewGuid(), Name = "Kullu", StateName = "Himachal Pradesh", IsActive = true, BusinessOnboardingEnabled = false },
    ];

    private static List<CityDto> Cities(Guid districtId) =>
    [
        new() { Id = Guid.NewGuid(), DistrictId = districtId, Name = "Bhowali", Latitude = 29.38m, Longitude = 79.52m },
        new() { Id = Guid.NewGuid(), DistrictId = districtId, Name = "Haldwani", Latitude = 29.2183m, Longitude = 79.5130m },
        new() { Id = Guid.NewGuid(), DistrictId = districtId, Name = "Ramnagar", Latitude = null, Longitude = null },
    ];

    private static void AssertSame<T>(IEnumerable<T> expected, JsonElement data)
        => Assert.Equal(JsonSerializer.Serialize(expected), data.GetRawText());

    [Fact]
    public async Task GetDistricts_uses_projection_keeps_order_and_caches()
    {
        var (uow, d, _) = Uow();
        d.Rows = Districts;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var (status, data, _) = CityFixture.Read(await AdminHandlers.GetDistricts(uow, cache));
        Assert.Equal(200, status);
        AssertSame(Districts, data);
        Assert.Equal([nameof(IDistrictRepository.GetAllDtosAsync)], d.Calls);

        var (status2, data2, _) = CityFixture.Read(await AdminHandlers.GetDistricts(uow, cache));
        Assert.Equal(200, status2);
        AssertSame(Districts, data2);
        Assert.Single(d.Calls);
    }

    [Fact]
    public async Task GetCities_by_district_uses_projection_keeps_order_and_caches()
    {
        var (uow, _, c) = Uow();
        var districtId = Guid.NewGuid();
        c.Rows = Cities(districtId);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var (status, data, _) = CityFixture.Read(await AdminHandlers.GetCities(districtId, uow, cache));
        Assert.Equal(200, status);
        AssertSame(c.Rows, data);
        Assert.Equal([nameof(ICityRepository.GetDtosByDistrictIdAsync)], c.Calls);
        Assert.Equal(districtId, c.SeenDistrict);
        Assert.True(cache.TryGetValue($"cities_{districtId}", out _));

        var (_, data2, _) = CityFixture.Read(await AdminHandlers.GetCities(districtId, uow, cache));
        AssertSame(c.Rows, data2);
        Assert.Single(c.Calls);
    }

    [Fact]
    public async Task GetCities_all_uses_projection_keeps_order_and_caches()
    {
        var (uow, _, c) = Uow();
        c.Rows = Cities(Guid.NewGuid());
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var (status, data, _) = CityFixture.Read(await AdminHandlers.GetCities(null, uow, cache));
        Assert.Equal(200, status);
        AssertSame(c.Rows, data);
        Assert.Equal([nameof(ICityRepository.GetAllDtosAsync)], c.Calls);
        Assert.True(cache.TryGetValue("cities_all", out _));

        var (_, data2, _) = CityFixture.Read(await AdminHandlers.GetCities(null, uow, cache));
        AssertSame(c.Rows, data2);
        Assert.Single(c.Calls);
    }
}

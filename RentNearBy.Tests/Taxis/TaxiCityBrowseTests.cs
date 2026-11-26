using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Taxis;

public class TaxiCityBrowseTests
{
    private static Task<Microsoft.AspNetCore.Http.IResult> Browse(
        long cityKey, ICityResolver resolver, IUnitOfWork uow, string? category = null, int page = 1, int pageSize = 10, bool localRate = true,
        int? people = null, double? lat = null, double? lng = null, double? radiusKm = null)
        => TaxiHandlers.GetBrowse(null, null, category, cityKey, uow, resolver, CityFixture.NoRedis(), page, pageSize, false, localRate,
            people, null, lat, lng, radiusKm);

    private static List<Guid> Ids(System.Text.Json.JsonElement data) =>
        data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("Id").GetGuid()).ToList();

    [Fact]
    public async Task Haldwani_and_kathgodam_taxis_are_separate_lists()
    {
        var (uow, _, taxis) = CityFixture.Uow();
        var h = CityFixture.Taxi(CityFixture.NearHaldwani);
        var k = CityFixture.Taxi(CityFixture.NearKathgodam);
        taxis.All = [h, k];
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var inH = CityFixture.Read(await Browse(CityFixture.Haldwani.CityKey, r, uow));
        var inK = CityFixture.Read(await Browse(CityFixture.Kathgodam.CityKey, r, uow));

        Assert.Equal([h.Item.Id], Ids(inH.Data));
        Assert.Equal([k.Item.Id], Ids(inK.Data));
    }

    [Fact]
    public async Task District_and_radius_are_never_needed_and_the_far_pickup_is_ranked_not_cut()
    {
        var (uow, _, taxis) = CityFixture.Uow();
        var nearBase = CityFixture.Taxi(CityFixture.NearHaldwani);
        var farBase = CityFixture.Taxi((29.30, 79.53));
        taxis.All = [farBase, nearBase];
        var r = CityFixture.Resolver(CityFixture.Haldwani);

        var result = CityFixture.Read(await Browse(CityFixture.Haldwani.CityKey, r, uow,
            lat: CityFixture.Haldwani.Latitude, lng: CityFixture.Haldwani.Longitude, radiusKm: 1));

        Assert.Equal(200, result.Status);
        Assert.Equal(2, Ids(result.Data).Count);
        Assert.Equal(nearBase.Item.Id, Ids(result.Data)[0]);
    }

    [Fact]
    public async Task Unknown_city_is_empty_and_atlas_down_is_503()
    {
        var (uow, _, taxis) = CityFixture.Uow();
        taxis.All = [CityFixture.Taxi(CityFixture.NearHaldwani)];

        var unknown = CityFixture.Read(await Browse(999, CityFixture.Resolver(CityFixture.Haldwani), uow));
        var down = CityFixture.Read(await Browse(CityFixture.Haldwani.CityKey, CityFixture.NoSnapshot(), uow));

        Assert.Empty(Ids(unknown.Data));
        Assert.Equal(503, down.Status);
        Assert.Equal(TripErrorTypes.CitiesUnavailable, down.ErrorType);
    }

    [Fact]
    public async Task Old_shape_without_district_or_city_key_is_still_a_400()
    {
        var (uow, _, taxis) = CityFixture.Uow();

        var result = await TaxiHandlers.GetBrowse(null, null, null, null, uow, CityFixture.Resolver(CityFixture.Haldwani), CityFixture.NoRedis());

        Assert.Equal(400, CityFixture.Read(result).Status);
        Assert.Equal(0, taxis.Calls);
    }

    [Fact]
    public void Category_filter_paging_and_has_more()
    {
        var all = Enumerable.Range(0, 25).Select(_ => CityFixture.Taxi(CityFixture.NearHaldwani, category: "SUV")).ToList();
        all.Add(CityFixture.Taxi(CityFixture.NearHaldwani, category: "Sedan"));

        var suv = TaxiCityList.Page(all, "SUV", null, 1, 10);
        var last = TaxiCityList.Page(all, "SUV", null, 3, 10);

        Assert.Equal(10, suv.Items.Count);
        Assert.True(suv.HasMore);
        Assert.Equal(5, last.Items.Count);
        Assert.False(last.HasMore);
    }

    [Fact]
    public void Need_ranks_seat_fit_first_and_fills_the_trip_fields()
    {
        var small = CityFixture.Taxi(CityFixture.NearHaldwani, seats: 4);
        var big = CityFixture.Taxi((29.30, 79.53), seats: 7);

        var page = TaxiCityList.Page([small, big], null, new TaxiLocalNeed(6, 0, CityFixture.Haldwani.Latitude, CityFixture.Haldwani.Longitude), 1, 10);

        Assert.Equal([big.Item.Id, small.Item.Id], page.Items.Select(t => t.Id));
        Assert.True(page.Items[0].TripSeatsFit);
        Assert.NotNull(page.Items[0].TripPickupKm);
    }

    // Contract R11: browse always lists one IST day; absent = today, so a taxi off or booked today never shows by default.
    public class DayStub : DispatchProxy
    {
        public List<DateOnly?> Days = [];

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            switch (m?.Name)
            {
                case nameof(ITaxiListingRepository.BrowseLocalNearAsync):
                    Days.Add((DateOnly?)a![6]);
                    return Task.FromResult<IReadOnlyList<(TaxiBrowseDto Item, double Lat, double Lng)>>([]);
                case nameof(ITaxiListingRepository.BrowseLocalPagedAsync) when a!.Length == 9:
                    Days.Add((DateOnly?)a![8]);
                    return Task.FromResult<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)>(([], false));
                default:
                    throw new NotSupportedException(m?.Name);
            }
        }
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    // 20:00 UTC on 26 Nov is 01:30 IST on 27 Nov.
    private static readonly DateOnly IstToday = new(2026, 11, 27);

    private static IServiceProvider At2000Utc() =>
        new ServiceCollection().AddSingleton<TimeProvider>(new FixedClock(new DateTime(2026, 11, 26, 20, 0, 0, DateTimeKind.Utc))).BuildServiceProvider();

    private static (IUnitOfWork Uow, DayStub Taxis) DayUow()
    {
        var (uow, _, _) = CityFixture.Uow();
        var taxis = DispatchProxy.Create<ITaxiListingRepository, DayStub>();
        ((CityFixture.UowStub)(object)uow).Taxis = taxis;
        return (uow, (DayStub)(object)taxis);
    }

    private static Task<Microsoft.AspNetCore.Http.IResult> CityBrowseOn(IUnitOfWork uow, string? date)
        => TaxiHandlers.GetBrowse(null, null, null, CityFixture.Haldwani.CityKey, uow, CityFixture.Resolver(CityFixture.Haldwani), At2000Utc(),
            date: date);

    [Fact]
    public async Task City_browse_without_a_date_lists_ist_today()
    {
        var (uow, taxis) = DayUow();
        Assert.Equal(200, CityFixture.Read(await CityBrowseOn(uow, null)).Status);
        Assert.Equal(IstToday, Assert.Single(taxis.Days));
    }

    [Fact]
    public async Task District_browse_without_a_date_lists_ist_today()
    {
        var (uow, taxis) = DayUow();
        var r = await TaxiHandlers.GetBrowse(Guid.NewGuid(), null, null, null, uow, CityFixture.Resolver(CityFixture.Haldwani), At2000Utc());
        Assert.Equal(200, CityFixture.Read(r).Status);
        Assert.Equal(IstToday, Assert.Single(taxis.Days));
    }

    [Theory]
    [InlineData("2026-12-01", 2026, 12, 1)]
    [InlineData("2026-11-20", 2026, 11, 27)]
    public async Task City_browse_lists_the_sent_day_and_a_past_day_reads_as_today(string date, int y, int m, int d)
    {
        var (uow, taxis) = DayUow();
        Assert.Equal(200, CityFixture.Read(await CityBrowseOn(uow, date)).Status);
        Assert.Equal(new DateOnly(y, m, d), Assert.Single(taxis.Days));
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("soon")]
    [InlineData("2027-02-26")]
    public async Task Browse_with_a_malformed_or_too_far_date_is_invalid_date_and_reads_nothing(string date)
    {
        var (uow, taxis) = DayUow();
        var read = CityFixture.Read(await CityBrowseOn(uow, date));
        Assert.Equal(400, read.Status);
        Assert.Equal("InvalidDate", read.ErrorType);
        Assert.Empty(taxis.Days);
    }

    [Fact]
    public void City_cache_key_holds_the_city_not_a_coordinate_or_radius()
    {
        Assert.Equal("taxis:city:210000001:rate", TaxiCache.CityKey(210000001, true));
        Assert.Equal("taxis:city:210000001", TaxiCache.CityKey(210000001, false));
        Assert.NotEqual(TaxiCache.CityKey(210000001, true), TaxiCache.CityKey(210000002, true));
    }
}

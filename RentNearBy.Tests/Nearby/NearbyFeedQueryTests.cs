using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.Nearby;

// SQL text checks run everywhere; the PostGIS checks below skip without RENTNEARBY_TEST_DB.
public class NearbyFeedQuerySqlTests
{
    [Theory]
    [InlineData(NearbyKind.Stay, "\"StayListings\"", "s.\"Location\"")]
    [InlineData(NearbyKind.Taxi, "\"TaxiListings\"", "t.\"LocationCoarse\"")]
    [InlineData(NearbyKind.StayJob, "\"StayJobs\"", "s.\"Location\"")]
    public void Page_sql_uses_one_distance_expression_for_select_order_and_keyset(NearbyKind kind, string table, string loc)
    {
        var sql = NearbyFeedQuery.BuildPageSql(kind, true, new NearbyFilter());
        var expr = $"({loc} <-> ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography)";
        Assert.Contains(table, sql);
        Assert.Equal(4, sql.Split(expr).Length - 1); // select, two keyset terms, order by
        Assert.Contains("ORDER BY " + expr + ",", sql);
        Assert.DoesNotContain("ST_DWithin", sql);
        Assert.DoesNotContain("ST_Distance", sql);
    }

    [Fact]
    public void Page_sql_has_no_distance_cutoff_and_hides_dead_rows()
    {
        var sql = NearbyFeedQuery.BuildPageSql(NearbyKind.Stay, false, new NearbyFilter());
        Assert.Contains("s.\"Status\" = 'Approved'", sql);
        Assert.Contains("s.\"AcceptingGuests\"", sql);
        Assert.Contains("NOT s.\"IsDeleted\"", sql);
        Assert.Contains("d.\"IsActive\"", sql);
        Assert.DoesNotContain("@maxM", sql);
    }

    [Fact]
    public void Filters_are_parameters_never_text()
    {
        var f = new NearbyFilter(StayTypeId: Guid.NewGuid(), Meals: "x'; DROP TABLE y;--", ExcludeIds: [Guid.NewGuid()]);
        var sql = NearbyFeedQuery.BuildPageSql(NearbyKind.Stay, false, f);
        Assert.DoesNotContain("DROP", sql);
        Assert.Contains("@stayType", sql);
        Assert.Contains("@meals", sql);
        Assert.Contains("<> ALL(@ex)", sql);
    }
}

[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public class NearbyFeedQueryTests(TestDatabase db)
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Delhi = (28.6139, 77.2090);
    private static readonly (double Lat, double Lng) Pune = (18.5204, 73.8567);

    private async Task<Guid> StayAtAsync(JourneyHarness h, (double Lat, double Lng) at, string? status = null)
    {
        var owner = await h.AddStayOwnerAsync();
        var id = await h.AddStayAsync(owner);
        await using var ctx = db.CreateContext();
        await ctx.StayListings.Where(s => s.Id == id).ExecuteUpdateAsync(x => x
            .SetProperty(s => s.Latitude, (decimal)at.Lat).SetProperty(s => s.Longitude, (decimal)at.Lng));
        if (status != null)
            await ctx.StayListings.Where(s => s.Id == id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, status));
        return id;
    }

    private async Task<List<NearbyRow>> WalkAsync(double lat, double lng, int take)
    {
        var all = new List<NearbyRow>();
        NearbyToken? after = null;
        for (var i = 0; i < 5000; i++)
        {
            await using var ctx = db.CreateContext();
            var rows = await new NearbyFeedQuery(ctx).PageAsync(NearbyKind.Stay, lat, lng, after, take, new NearbyFilter());
            all.AddRange(rows);
            if (rows.Count < take) break;
            after = new NearbyToken(rows[^1].DistanceMeters, rows[^1].Id);
        }
        return all;
    }

    [IntegrationFact]
    public async Task Order_matches_haversine_and_there_is_no_cutoff()
    {
        await using var h = new JourneyHarness(db);
        var mineAt = new[] { (Pune, await StayAtAsync(h, Pune)), (Delhi, await StayAtAsync(h, Delhi)), (Nainital, await StayAtAsync(h, Nainital)) };
        var rows = await WalkAsync(Haldwani.Lat, Haldwani.Lng, 40);
        var mine = rows.Where(r => mineAt.Any(f => f.Item2 == r.Id)).Select(r => r.Id).ToList();
        var expected = mineAt.OrderBy(f => NearbyContext.HaversineKm(Haldwani.Lat, Haldwani.Lng, f.Item1.Lat, f.Item1.Lng))
            .Select(f => f.Item2).ToList();
        Assert.Equal(expected, mine);
        Assert.True(rows.Single(r => r.Id == mineAt[0].Item2).DistanceMeters > 1_000_000);
    }

    [IntegrationFact]
    public async Task Keyset_paging_has_no_gap_or_duplicate()
    {
        await using var h = new JourneyHarness(db);
        for (var i = 0; i < 4; i++) await StayAtAsync(h, Haldwani); // equal distances, Id breaks the tie
        await StayAtAsync(h, Nainital);
        var small = await WalkAsync(Haldwani.Lat, Haldwani.Lng, 2);
        var big = await WalkAsync(Haldwani.Lat, Haldwani.Lng, 40);
        Assert.Equal(big.Select(r => r.Id), small.Select(r => r.Id));
        Assert.Equal(small.Count, small.Select(r => r.Id).Distinct().Count());
        Assert.Equal(small.OrderBy(r => r.DistanceMeters).ThenBy(r => r.Id).Select(r => r.Id), small.Select(r => r.Id));
    }

    [IntegrationFact]
    public async Task Unapproved_deleted_and_paused_stays_never_appear()
    {
        await using var h = new JourneyHarness(db);
        var live = await StayAtAsync(h, Haldwani);
        var pending = await StayAtAsync(h, Haldwani, StayStatuses.PendingReview);
        var deleted = await StayAtAsync(h, Haldwani);
        var paused = await StayAtAsync(h, Haldwani);
        await using (var ctx = db.CreateContext())
        {
            await ctx.StayListings.Where(s => s.Id == deleted).ExecuteUpdateAsync(x => x.SetProperty(s => s.IsDeleted, true));
            await ctx.StayListings.Where(s => s.Id == paused).ExecuteUpdateAsync(x => x.SetProperty(s => s.AcceptingGuests, false));
        }
        var ids = (await WalkAsync(Haldwani.Lat, Haldwani.Lng, 40)).Select(r => r.Id).ToHashSet();
        Assert.Contains(live, ids);
        Assert.DoesNotContain(pending, ids);
        Assert.DoesNotContain(deleted, ids);
        Assert.DoesNotContain(paused, ids);
    }

    [IntegrationFact]
    public async Task A_stay_in_a_switched_off_district_is_hidden()
    {
        var district = new District
        {
            Id = Guid.NewGuid(), Name = "Nearby test " + Guid.NewGuid().ToString("N")[..6],
            StateName = "Test", IsActive = false, CreatedAt = DateTime.UtcNow,
        };
        await using (var ctx = db.CreateContext()) { ctx.Districts.Add(district); await ctx.SaveChangesAsync(); }
        try
        {
            await using var h = new JourneyHarness(db);
            var hidden = await StayAtAsync(h, Haldwani);
            await using (var ctx = db.CreateContext())
                await ctx.StayListings.Where(s => s.Id == hidden).ExecuteUpdateAsync(x => x.SetProperty(s => s.DistrictId, district.Id));
            var ids = (await WalkAsync(Haldwani.Lat, Haldwani.Lng, 40)).Select(r => r.Id).ToHashSet();
            Assert.DoesNotContain(hidden, ids);
        }
        finally
        {
            await using var ctx = db.CreateContext();
            await ctx.StayListings.Where(s => s.DistrictId == district.Id).ExecuteDeleteAsync();
            await ctx.Districts.Where(d => d.Id == district.Id).ExecuteDeleteAsync();
        }
    }

    [IntegrationFact]
    public async Task Featured_is_only_paid_and_only_within_25_km()
    {
        await using var h = new JourneyHarness(db);
        var near = await StayAtAsync(h, Nainital);
        var farPaid = await StayAtAsync(h, Pune);
        var free = await StayAtAsync(h, Haldwani);
        await using var ctx = db.CreateContext();
        await ctx.StayListings.Where(s => s.Id == near || s.Id == farPaid)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.PriorityRank, (short)1));
        var rows = await new NearbyFeedQuery(ctx).FeaturedAsync(
            NearbyKind.Stay, Nainital.Lat + 0.01, Nainital.Lng, NearbyRules.FeaturedMaxKm, 10, new NearbyFilter());
        var ids = rows.Select(r => r.Id).ToList();
        Assert.Contains(near, ids);
        Assert.DoesNotContain(farPaid, ids);
        Assert.DoesNotContain(free, ids);
    }

    [IntegrationFact]
    public async Task Hydrator_returns_no_phone_or_coordinates_and_keeps_distance()
    {
        await using var h = new JourneyHarness(db);
        var id = await StayAtAsync(h, Nainital);
        await using var ctx = db.CreateContext();
        var map = await StayFeedHydrator.LoadAsync(ctx, [new NearbyRow(id, 1500.0)]);
        Assert.Equal(1.5, map[id].DistanceKm);
        Assert.True(map[id].IsNear);
        var json = System.Text.Json.JsonSerializer.Serialize(map[id]);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", json, StringComparison.OrdinalIgnoreCase);
    }
}

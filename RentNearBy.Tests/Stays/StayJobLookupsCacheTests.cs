using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Stays;

// Public active lookups are cached for every caller; only a successful admin write removes the key.
public class StayJobLookupsCacheTests
{
    private static MemoryCache Cache() => new(new MemoryCacheOptions());

    private static StayJobLookupsDto Seeded(MemoryCache cache)
    {
        var dto = new StayJobLookupsDto();
        cache.Set(StayJobHandlers.LookupsCacheKey, dto);
        return dto;
    }

    [Fact]
    public async Task Second_read_does_not_call_the_loader()
    {
        using var cache = Cache();
        var loads = 0;
        var dto = new StayJobLookupsDto();
        Task<StayJobLookupsDto> Load() { loads++; return Task.FromResult(dto); }

        var a = await StayJobHandlers.GetActiveLookupsCachedAsync(cache, Load);
        var b = await StayJobHandlers.GetActiveLookupsCachedAsync(cache, Load);

        Assert.Equal(1, loads);
        Assert.Same(dto, a);
        Assert.Same(dto, b);
        Assert.True(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));
        Assert.Equal("stayjob_lookups", StayJobHandlers.LookupsCacheKey);
    }

    [Fact]
    public async Task Rejected_admin_writes_keep_the_key()
    {
        using var cache = Cache();
        var dto = Seeded(cache);
        var validator = new UpsertJobLookupRequestValidator();

        await AdminStayJobHandlers.CreateLookup("nope", new UpsertJobLookupRequest { Name = "X" }, validator, null!, cache);
        await AdminStayJobHandlers.CreateLookup(StayJobLookupKinds.JobTypes, new UpsertJobLookupRequest { Name = " " }, validator, null!, cache);
        await AdminStayJobHandlers.UpdateLookup("nope", Guid.NewGuid(), new UpsertJobLookupRequest { Name = "X" }, validator, null!, cache);
        await AdminStayJobHandlers.UpdateLookup(StayJobLookupKinds.JobTypes, Guid.NewGuid(), new UpsertJobLookupRequest { Name = "" }, validator, null!, cache);
        await AdminStayJobHandlers.DeleteLookup("nope", Guid.NewGuid(), null!, cache);

        Assert.True(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out StayJobLookupsDto? still));
        Assert.Same(dto, still);
    }
}

// Successful create, update and delete each remove the key (real PostgreSQL; skipped without RENTNEARBY_TEST_DB).
public class StayJobLookupsCacheIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Each_successful_admin_write_removes_the_key()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new UpsertJobLookupRequestValidator();
        var name = "Perf test " + Guid.NewGuid().ToString("N")[..8];

        await using var ctx = CreateContext();
        cache.Set(StayJobHandlers.LookupsCacheKey, new StayJobLookupsDto());
        var created = await AdminStayJobHandlers.CreateLookup(
            StayJobLookupKinds.JobTypes, new UpsertJobLookupRequest { Name = name, SortOrder = 1 }, validator, ctx, cache);
        Assert.False(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));
        var id = ctx.JobTypes.Single(x => x.Name == name).Id;

        cache.Set(StayJobHandlers.LookupsCacheKey, new StayJobLookupsDto());
        await AdminStayJobHandlers.CreateLookup(
            StayJobLookupKinds.JobTypes, new UpsertJobLookupRequest { Name = name, SortOrder = 1 }, validator, ctx, cache);
        Assert.True(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));

        await AdminStayJobHandlers.UpdateLookup(
            StayJobLookupKinds.JobTypes, id, new UpsertJobLookupRequest { Name = name + " b", SortOrder = 2 }, validator, ctx, cache);
        Assert.False(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));

        cache.Set(StayJobHandlers.LookupsCacheKey, new StayJobLookupsDto());
        await AdminStayJobHandlers.DeleteLookup(StayJobLookupKinds.JobTypes, Guid.NewGuid(), ctx, cache);
        Assert.True(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));

        await AdminStayJobHandlers.DeleteLookup(StayJobLookupKinds.JobTypes, id, ctx, cache);
        Assert.False(cache.TryGetValue(StayJobHandlers.LookupsCacheKey, out _));
        Assert.NotNull(created);
    }
}

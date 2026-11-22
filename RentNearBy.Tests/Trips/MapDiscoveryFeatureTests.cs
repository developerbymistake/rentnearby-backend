using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Trips;

public class MapDiscoveryFeatureTests
{
    private sealed class NoDbScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no db in unit tests");
    }

    private sealed class FakeLoadReader(IMemoryCache cache, Func<Task<bool>>? load = null)
        : MapDiscoveryFeatureReader(cache, new NoDbScopes(), NullLogger<MapDiscoveryFeatureReader>.Instance)
    {
        public int Loads;
        public bool Stored;
        protected override async Task<bool> LoadAsync()
        {
            Interlocked.Increment(ref Loads);
            return load != null ? await load() : Stored;
        }
    }

    private sealed class StubReader(bool enabled) : IMapDiscoveryFeatureReader
    {
        public int Invalidations;
        public Task<bool> IsEnabledAsync() => Task.FromResult(enabled);
        public void Invalidate() => Invalidations++;
    }

    private static readonly UpdateTripExecutionRequestValidator Validator = new();

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public void Key_And_CacheKey()
    {
        Assert.Equal("MAP_DISCOVERY_ENABLED", AppFeatureKeys.MapDiscoveryEnabled);
        Assert.Equal("config_map_discovery", IMapDiscoveryFeatureReader.CacheKey);
    }

    [Fact]
    public async Task Value_IsCached_SecondReadMakesNoLoad()
    {
        var reader = new FakeLoadReader(NewCache()) { Stored = true };

        Assert.True(await reader.IsEnabledAsync());
        Assert.True(await reader.IsEnabledAsync());
        Assert.Equal(1, reader.Loads);
    }

    [Fact]
    public async Task Invalidate_ReloadsChangedValue()
    {
        var reader = new FakeLoadReader(NewCache()) { Stored = true };
        Assert.True(await reader.IsEnabledAsync());
        reader.Stored = false;
        reader.Invalidate();

        Assert.False(await reader.IsEnabledAsync());
        Assert.Equal(2, reader.Loads);
    }

    [Fact]
    public async Task LoadRacingInvalidate_IsNotCached()
    {
        var gate = new TaskCompletionSource<bool>();
        var started = new TaskCompletionSource();
        var cache = NewCache();
        var first = true;
        var reader = new FakeLoadReader(cache, async () =>
        {
            if (!first) return false;
            first = false;
            started.SetResult();
            return await gate.Task;
        });

        var racing = reader.IsEnabledAsync();
        await started.Task;
        reader.Invalidate();
        gate.SetResult(true);

        Assert.True(await racing);
        Assert.False(cache.TryGetValue(IMapDiscoveryFeatureReader.CacheKey, out bool _));
        Assert.False(await reader.IsEnabledAsync());
    }

    private static (IUnitOfWork Uow, List<string> Calls) Uow(AppFeatureFlag? flag)
    {
        var calls = new List<string>();
        var repo = TripTimerBatchLoadTests.Fake<IAppFeatureFlagRepository>.Create((m, a) =>
        {
            calls.Add(m.Name);
            if (m.Name == nameof(IAppFeatureFlagRepository.GetByKeyAsync)) Assert.Equal(AppFeatureKeys.MapDiscoveryEnabled, a?[0]);
            return m.Name switch
            {
                nameof(IAppFeatureFlagRepository.GetByKeyAsync) => Task.FromResult(flag),
                "UpdateAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(m.Name),
            };
        });
        var uow = TripTimerBatchLoadTests.Fake<IUnitOfWork>.Create((m, a) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "get_AppFeatureFlags" => repo,
                nameof(IUnitOfWork.SaveChangesAsync) => Task.FromResult(1),
                _ => throw new InvalidOperationException(m.Name),
            };
        });
        return (uow, calls);
    }

    private static ClaimsPrincipal Admin(Guid id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "t"));

    [Fact]
    public async Task AdminPut_UpdatesRow_InvalidatesAfterSave()
    {
        var flag = new AppFeatureFlag { FeatureKey = AppFeatureKeys.MapDiscoveryEnabled, IsEnabled = true };
        var (uow, calls) = Uow(flag);
        var reader = new StubReader(true);
        var admin = Guid.NewGuid();

        var result = await AdminMapDiscoveryHandlers.Update(new UpdateTripExecutionRequest(false, "rollback"), Validator,
            Admin(admin), uow, reader, NullLoggerFactory.Instance);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(flag.IsEnabled);
        Assert.Equal("rollback", flag.Reason);
        Assert.Equal(admin, flag.UpdatedByAdminId);
        Assert.Equal(1, reader.Invalidations);
        Assert.Contains(nameof(IUnitOfWork.SaveChangesAsync), calls);
    }

    [Fact]
    public async Task AdminPut_Invalid_400_NoInvalidate()
    {
        var (uow, _) = Uow(new AppFeatureFlag());
        var reader = new StubReader(true);

        var result = await AdminMapDiscoveryHandlers.Update(new UpdateTripExecutionRequest(null), Validator,
            Admin(Guid.NewGuid()), uow, reader, NullLoggerFactory.Instance);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, reader.Invalidations);
    }

    [Fact]
    public async Task AdminPut_MissingRow_404()
    {
        var (uow, _) = Uow(null);

        var result = await AdminMapDiscoveryHandlers.Update(new UpdateTripExecutionRequest(true), Validator,
            Admin(Guid.NewGuid()), uow, new StubReader(false), NullLoggerFactory.Instance);

        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task AdminGet_ReturnsRow()
    {
        var (uow, _) = Uow(new AppFeatureFlag { FeatureKey = AppFeatureKeys.MapDiscoveryEnabled, IsEnabled = true });

        var result = await AdminMapDiscoveryHandlers.Get(uow);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicConfig_FollowsReader_NoCache(bool enabled)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        var result = await ConfigHandlers.GetMapDiscovery(new StubReader(enabled), http);
        await result.ExecuteAsync(http);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("no-cache", http.Response.Headers.CacheControl.ToString());
        var data = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray()).RootElement.GetProperty("data");
        Assert.Equal(enabled, data.GetProperty("enabled").GetBoolean());
    }
}

// Needs PostgreSQL (RENTNEARBY_TEST_DB); NOT RUN on a PC without it.
public class MapDiscoveryFeatureIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task DeleteRowAsync()
    {
        await using var c = CreateContext();
        await c.AppFeatureFlags.Where(f => f.FeatureKey == AppFeatureKeys.MapDiscoveryEnabled).ExecuteDeleteAsync();
    }

    [IntegrationFact]
    public async Task Seeder_InsertsOnce_On_NeverOverwrites()
    {
        await DeleteRowAsync();
        try
        {
            await using (var c = CreateContext()) await DataSeeder.SeedAppFeatureFlagsAsync(c);
            await using (var c = CreateContext())
            {
                var row = await c.AppFeatureFlags.SingleAsync(f => f.FeatureKey == AppFeatureKeys.MapDiscoveryEnabled);
                Assert.True(row.IsEnabled);
                row.IsEnabled = false;
                await c.SaveChangesAsync();
            }

            await using (var c = CreateContext()) await DataSeeder.SeedAppFeatureFlagsAsync(c);
            await using (var c = CreateContext())
            {
                var rows = await c.AppFeatureFlags.Where(f => f.FeatureKey == AppFeatureKeys.MapDiscoveryEnabled).ToListAsync();
                Assert.False(Assert.Single(rows).IsEnabled);
            }
        }
        finally
        {
            await DeleteRowAsync();
            await using var c = CreateContext();
            await DataSeeder.SeedAppFeatureFlagsAsync(c);
        }
    }

    [IntegrationFact]
    public async Task Reader_MissingRow_IsOff()
    {
        await DeleteRowAsync();
        try
        {
            var services = new ServiceCollection().AddScoped(_ => CreateContext()).BuildServiceProvider();
            var reader = new MapDiscoveryFeatureReader(new MemoryCache(new MemoryCacheOptions()),
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MapDiscoveryFeatureReader>.Instance);

            Assert.False(await reader.IsEnabledAsync());
        }
        finally
        {
            await using var c = CreateContext();
            await DataSeeder.SeedAppFeatureFlagsAsync(c);
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Every Redis call is guarded. Without Redis no matrix is built (nowhere to keep it) and routes go straight to OSRM.
// IOsrmClient is resolved per call so the typed client's handler keeps rotating. The planner only peeks: a missing
// matrix is built in the background while the request uses the direct table.
public class TripRoadCache(
    ITripSpotCache spotCache,
    IOptions<TripPlannerOptions> options,
    IServiceProvider sp,
    ILogger<TripRoadCache> logger) : ITripRoadCache
{
    private const int LockWaitRounds = 10;
    private static readonly TimeSpan LockWaitDelay = TimeSpan.FromMilliseconds(300);
    private const int RouteFetchConcurrency = 4;
    // Blocks of a failed build are kept this long, so the next build fetches only the missing ones.
    private static readonly TimeSpan BlockTtl = TimeSpan.FromHours(1);

    private sealed class MatrixBlock
    {
        [JsonPropertyName("s")] public int[] Sec { get; set; } = [];
        [JsonPropertyName("m")] public int[] Metres { get; set; } = [];
    }

    private readonly ConcurrentDictionary<long, Lazy<Task<CityRoadMatrix?>>> _inflight = new();

    public async Task<CityRoadMatrix?> GetCityMatrixAsync(long cityKey, CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return null;

        var hit = await ReadMatrixAsync(redis, cityKey);
        if (hit != null) return hit;

        return await SharedBuild(redis, cityKey).WaitAsync(ct);
    }

    public async Task<CityRoadMatrix?> PeekCityMatrixAsync(long cityKey, CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return null;

        var hit = await ReadMatrixAsync(redis, cityKey);
        if (hit != null) return hit;

        _ = SharedBuild(redis, cityKey);
        return null;
    }

    private Task<CityRoadMatrix?> SharedBuild(IConnectionMultiplexer redis, long cityKey)
        => _inflight.GetOrAdd(cityKey, key => new Lazy<Task<CityRoadMatrix?>>(() => Task.Run(() => LoadOnceAsync(redis, key)))).Value;

    public async Task InvalidateAsync(long cityKey)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { TripCacheKeys.Matrix(cityKey), TripCacheKeys.MatrixLock(cityKey) });
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: invalidate of {CityKey} failed: {Error}", cityKey, ex.Message);
        }
    }

    public async Task<JsonElement?> GetRouteGeometryAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
        => (await GetRouteGeometriesAsync(new[] { points }, ct))[0];

    public async Task<JsonElement?[]> GetRouteGeometriesAsync(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> routes, CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var keys = routes.Select(TripCacheKeys.Route).ToArray();
        var results = await Task.WhenAll(keys.Select(key => ReadRouteAsync(redis, key)));

        var misses = Enumerable.Range(0, routes.Count).Where(i => results[i] == null).ToList();
        if (misses.Count == 0) return results;

        var osrm = sp.GetRequiredService<IOsrmClient>();
        using var gate = new SemaphoreSlim(RouteFetchConcurrency);
        await Task.WhenAll(misses.Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var route = await osrm.RouteAsync(routes[i], ct);
                if (route == null) return;
                await WriteAsync(redis, keys[i], route.Geometry.GetRawText(), TimeSpan.FromDays(options.Value.RouteCacheTtlDays));
                results[i] = route.Geometry;
            }
            finally
            {
                gate.Release();
            }
        }));
        return results;
    }

    private async Task<JsonElement?> ReadRouteAsync(IConnectionMultiplexer? redis, string key)
    {
        var cached = await ReadAsync(redis, key);
        if (cached == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(cached);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    // The shared build never takes a caller's token: one cancelled request must not fail the others.
    private async Task<CityRoadMatrix?> LoadOnceAsync(IConnectionMultiplexer redis, long cityKey)
    {
        try
        {
            return await LoadAsync(redis, cityKey);
        }
        finally
        {
            _inflight.TryRemove(cityKey, out _);
        }
    }

    private async Task<CityRoadMatrix?> LoadAsync(IConnectionMultiplexer redis, long cityKey)
    {
        var o = options.Value;
        var lockKey = TripCacheKeys.MatrixLock(cityKey);
        var acquired = await TryAcquireLockAsync(redis, lockKey, TimeSpan.FromSeconds(o.LockSeconds));
        if (acquired == null) return null;

        if (acquired == false)
        {
            for (var i = 0; i < LockWaitRounds; i++)
            {
                await Task.Delay(LockWaitDelay);
                var fresh = await ReadMatrixAsync(redis, cityKey);
                if (fresh != null) return fresh;
            }
            return null;
        }

        try
        {
            var again = await ReadMatrixAsync(redis, cityKey);
            if (again != null) return again;

            var spots = await spotCache.GetAsync(cityKey);
            var matrix = await BuildAsync(redis, cityKey, spots?.Spots, o);
            if (matrix == null) return null;

            await WriteAsync(redis, TripCacheKeys.Matrix(cityKey), JsonSerializer.Serialize(matrix), TimeSpan.FromHours(o.CacheTtlHours));
            return matrix;
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: matrix build for {CityKey} failed: {Error}", cityKey, ex.Message);
            return null;
        }
        finally
        {
            await DeleteAsync(redis, lockKey);
        }
    }

    // Blocks of half the coordinate limit, so a source block plus a destination block fits one request.
    // A failed block drops the whole matrix (a partial one is never cached), but the finished blocks are kept for BlockTtl
    // under a hash of the spot list, so the next build asks OSRM only for the missing ones.
    private async Task<CityRoadMatrix?> BuildAsync(IConnectionMultiplexer redis, long cityKey, List<TripSpotDto>? list, TripPlannerOptions o)
    {
        var spots = (list ?? new()).DistinctBy(s => s.Id).ToList();
        var n = spots.Count;
        if (n < 2 || n > o.CityMatrixMaxSpots) return null;

        var osrm = sp.GetRequiredService<IOsrmClient>();
        if (!osrm.IsConfigured) return null;

        var max = osrm.MaxTableCoordinates;
        var block = n <= max ? n : Math.Max(1, max / 2);
        var sec = new int[n * n];
        var metres = new int[n * n];
        var hash = block < n ? SpotsHash(spots) : null;
        var blockKeys = new List<RedisKey>();

        for (var r = 0; r < n; r += block)
        for (var c = 0; c < n; c += block)
        {
            var rows = Math.Min(block, n - r);
            var cols = Math.Min(block, n - c);
            if (r == c && rows == 1) continue;

            var blockKey = hash == null ? null : BlockKey(cityKey, hash, r, c);
            if (blockKey != null)
            {
                blockKeys.Add(blockKey);
                if (await ReadBlockAsync(redis, blockKey, rows * cols) is { } done)
                {
                    for (var i = 0; i < rows; i++)
                    for (var j = 0; j < cols; j++)
                    {
                        if (r + i == c + j) continue;
                        sec[(r + i) * n + c + j] = done.Sec[i * cols + j];
                        metres[(r + i) * n + c + j] = done.Metres[i * cols + j];
                    }
                    continue;
                }
            }

            var points = new List<(double Lat, double Lon)>(rows + cols);
            for (var i = 0; i < rows; i++) points.Add((spots[r + i].Latitude, spots[r + i].Longitude));
            var sources = Enumerable.Range(0, rows).ToList();
            List<int> destinations;
            if (r == c)
            {
                destinations = sources;
            }
            else
            {
                for (var j = 0; j < cols; j++) points.Add((spots[c + j].Latitude, spots[c + j].Longitude));
                destinations = Enumerable.Range(rows, cols).ToList();
            }

            var table = await osrm.TableAsync(points, sources, destinations);
            if (table == null)
            {
                logger.LogWarning("TripRoadCache: OSRM block {Row},{Col} failed, matrix not cached", r, c);
                return null;
            }

            var fresh = blockKey == null ? null : new MatrixBlock { Sec = new int[rows * cols], Metres = new int[rows * cols] };
            for (var i = 0; i < rows; i++)
            for (var j = 0; j < cols; j++)
            {
                var cell = (r + i) * n + c + j;
                if (r + i == c + j) continue;
                var s = table.Seconds[i, j];
                var m = table.Metres[i, j];
                if (s == null || m == null)
                {
                    sec[cell] = -1;
                    metres[cell] = -1;
                }
                else
                {
                    sec[cell] = CityRoadMatrix.StoredSeconds(s.Value);
                    metres[cell] = CityRoadMatrix.StoredMetres(m.Value);
                }
                if (fresh == null) continue;
                fresh.Sec[i * cols + j] = sec[cell];
                fresh.Metres[i * cols + j] = metres[cell];
            }
            if (fresh != null) await WriteAsync(redis, blockKey!, JsonSerializer.Serialize(fresh), BlockTtl);
        }

        if (blockKeys.Count > 0)
        {
            try
            {
                await redis.GetDatabase().KeyDeleteAsync(blockKeys.ToArray());
            }
            catch (Exception ex)
            {
                logger.LogWarning("TripRoadCache: block cleanup of {CityKey} failed: {Error}", cityKey, ex.Message);
            }
        }

        return new CityRoadMatrix { Ids = spots.Select(s => s.Id).ToList(), Sec = sec, Metres = metres };
    }

    private static string BlockKey(long cityKey, string hash, int row, int col) => $"trip:matrix:{cityKey}:blk:{hash}:{row}:{col}";

    // Ids and coordinates: a changed spot list never reuses an old block.
    private static string SpotsHash(List<TripSpotDto> spots)
    {
        var text = string.Join(';', spots.Select(s =>
            $"{s.Id},{s.Latitude.ToString("F5", CultureInfo.InvariantCulture)},{s.Longitude.ToString("F5", CultureInfo.InvariantCulture)}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private async Task<MatrixBlock?> ReadBlockAsync(IConnectionMultiplexer redis, string key, int cells)
    {
        var json = await ReadAsync(redis, key);
        if (json == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<MatrixBlock>(json);
            return value != null && value.Sec.Length == cells && value.Metres.Length == cells ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CityRoadMatrix?> ReadMatrixAsync(IConnectionMultiplexer redis, long cityKey)
    {
        var json = await ReadAsync(redis, TripCacheKeys.Matrix(cityKey));
        if (json == null) return null;
        try
        {
            var matrix = JsonSerializer.Deserialize<CityRoadMatrix>(json);
            return matrix is { IsValid: true } ? matrix : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("TripRoadCache: matrix of {CityKey} unreadable: {Error}", cityKey, ex.Message);
            return null;
        }
    }

    private async Task<string?> ReadAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: read of {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private async Task WriteAsync(IConnectionMultiplexer? redis, string key, string value, TimeSpan ttl)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(key, value, ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: write of {Key} failed: {Error}", key, ex.Message);
        }
    }

    // null = Redis failed (no cross-process lock, so no build).
    private async Task<bool?> TryAcquireLockAsync(IConnectionMultiplexer redis, string key, TimeSpan ttl)
    {
        try
        {
            return await redis.GetDatabase().StringSetAsync(key, "1", ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: lock {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private async Task DeleteAsync(IConnectionMultiplexer redis, string key)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripRoadCache: delete of {Key} failed: {Error}", key, ex.Message);
        }
    }
}

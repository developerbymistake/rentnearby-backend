using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// No database anywhere. Every Redis call is guarded (no Redis = memory + Atlas only).
// IAtlasClient is resolved per call so the typed client's handler keeps rotating.
public class TripMapPackService(
    IOptions<TripPlannerOptions> options,
    IServiceProvider sp,
    ILogger<TripMapPackService> logger,
    TimeProvider? clock = null) : ITripMapPackService
{
    private const int LockWaitRounds = 10;
    private static readonly TimeSpan LockWaitDelay = TimeSpan.FromMilliseconds(300);
    // Gap between two forced index refreshes, and the retry delay after a failed refresh.
    internal static readonly TimeSpan ForceGap = TimeSpan.FromSeconds(10);
    // After an Invalidate a known list waits this long for Atlas, then answers the known list (the refresh goes on).
    internal static readonly TimeSpan InvalidatedWait = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private TripMapPackIndex? _index;
    private DateTimeOffset _indexAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastForced = DateTimeOffset.MinValue;
    private Lazy<Task>? _indexFlight;
    private bool _invalidated;
    private long _invalidations;

    private readonly ConcurrentDictionary<Guid, TripMapPackBytes> _bytes = new();
    private readonly ConcurrentDictionary<(Guid, string), Lazy<Task<(TripMapPackBytes?, bool)>>> _inflight = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _failedAt = new();

    private TimeSpan Revalidate => TimeSpan.FromSeconds(Math.Max(1, options.Value.CityCatalogRevalidateSeconds));

    public async Task<IReadOnlyList<TripMapPackInfo>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            TripMapPackIndex? index;
            DateTimeOffset at;
            bool invalidated;
            lock (_gate) { index = _index; at = _indexAt; invalidated = _invalidated; }
            if (_clock.GetUtcNow() - at < Revalidate) return index?.Packs ?? (IReadOnlyList<TripMapPackInfo>)[];

            // Stale while revalidate: a list that is only old answers at once; a process told the data changed
            // (Invalidate) waits up to InvalidatedWait; a process that knows nothing waits for the refresh.
            if (index != null && !invalidated)
            {
                _ = RefreshIndexAsync(CancellationToken.None);
                return index.Packs;
            }

            if (index != null)
            {
                try
                {
                    await RefreshIndexAsync(ct).WaitAsync(InvalidatedWait, ct);
                }
                catch (TimeoutException)
                {
                }
            }
            else
            {
                await RefreshIndexAsync(ct);
            }
            lock (_gate) return _index?.Packs ?? (IReadOnlyList<TripMapPackInfo>)[];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("TripMapPackService: pack list failed: {Error}", ex.Message);
            lock (_gate) return _index?.Packs ?? (IReadOnlyList<TripMapPackInfo>)[];
        }
    }

    public bool PacksKnown
    {
        get { lock (_gate) return _index != null; }
    }

    public void Invalidate()
    {
        lock (_gate) { _indexAt = DateTimeOffset.MinValue; _invalidated = true; _invalidations++; }
    }

    public async Task<(TripMapPackBytes? Pack, bool Unavailable)> GetAsync(Guid stateId, string version, CancellationToken ct = default)
    {
        if (!TripMapPackKeys.IsVersion(version)) return (null, false);

        if (_bytes.TryGetValue(stateId, out var mem) && mem.Version == version) return (mem, false);

        var current = await CurrentVersionAsync(stateId, ct);
        if (current != version)
        {
            if (await ForceRefreshAsync(ct)) current = await CurrentVersionAsync(stateId, ct);
            if (current != version) return (null, false);
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        var hit = await ReadBytesAsync(redis, TripMapPackKeys.Pack(stateId, version));
        if (hit != null) return (Remember(stateId, version, hit), false);

        var key = (stateId, version);
        // A failed Atlas load is answered from memory for ForceGap instead of calling Atlas again at once.
        if (_failedAt.TryGetValue(key, out var failedAt) && _clock.GetUtcNow() - failedAt < ForceGap) return (null, true);
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<(TripMapPackBytes?, bool)>>(() => LoadOnceAsync(redis, k.Item1, k.Item2)));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<string?> CurrentVersionAsync(Guid stateId, CancellationToken ct)
    {
        var packs = await ListAsync(ct);
        foreach (var p in packs)
            if (p.StateId == stateId) return p.Version;
        return null;
    }

    // At most one forced refresh per ForceGap per process; false = skipped.
    private async Task<bool> ForceRefreshAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now - _lastForced < ForceGap) return false;
            _lastForced = now;
            _indexAt = DateTimeOffset.MinValue;
        }
        await RefreshIndexAsync(ct);
        return true;
    }

    private async Task RefreshIndexAsync(CancellationToken ct)
    {
        Lazy<Task> flight;
        lock (_gate) flight = _indexFlight ??= new Lazy<Task>(RunIndexRefreshAsync);
        await flight.Value.WaitAsync(ct);
    }

    private async Task RunIndexRefreshAsync()
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        long seen;
        lock (_gate) seen = _invalidations;
        try
        {
            var atlas = sp.GetRequiredService<IAtlasClient>();
            if (!atlas.IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");
            var packs = await atlas.GetMapPacksAsync();
            var now = _clock.GetUtcNow();
            var index = new TripMapPackIndex { FetchedAt = now.UtcDateTime, Packs = packs };
            lock (_gate) { _index = index; MarkFresh(now, seen); }
            await WriteAsync(redis, TripMapPackKeys.Index, JsonSerializer.Serialize(index, TripJson.Options));
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: Atlas pack list unavailable: {Error}", ex.Message);
            TripMapPackIndex? known;
            lock (_gate) known = _index;
            known ??= await ReadIndexAsync(redis);
            // Keep the last known list; retry Atlas after ForceGap instead of on every request.
            var retryAt = _clock.GetUtcNow() - Revalidate + ForceGap;
            lock (_gate) { _index = known; MarkFresh(retryAt, seen); }
        }
        finally
        {
            lock (_gate) _indexFlight = null;
        }
    }

    // An Invalidate that landed while this refresh was reading Atlas keeps the list stale, so the next call reloads.
    private void MarkFresh(DateTimeOffset at, long seen)
    {
        if (_invalidations != seen) return;
        _indexAt = at;
        _invalidated = false;
    }

    private async Task<(TripMapPackBytes?, bool)> LoadOnceAsync(IConnectionMultiplexer? redis, Guid stateId, string version)
    {
        try
        {
            var result = await LoadAsync(redis, stateId, version);
            if (result.Item2) _failedAt[(stateId, version)] = _clock.GetUtcNow();
            else _failedAt.TryRemove((stateId, version), out _);
            return result;
        }
        finally
        {
            _inflight.TryRemove((stateId, version), out _);
        }
    }

    private async Task<(TripMapPackBytes?, bool)> LoadAsync(IConnectionMultiplexer? redis, Guid stateId, string version)
    {
        var packKey = TripMapPackKeys.Pack(stateId, version);
        var lockKey = TripMapPackKeys.Lock(stateId, version);
        var acquired = await TryAcquireLockAsync(redis, lockKey, TimeSpan.FromSeconds(options.Value.LockSeconds));

        if (acquired == false)
        {
            for (var i = 0; i < LockWaitRounds; i++)
            {
                await Task.Delay(LockWaitDelay);
                var other = await ReadBytesAsync(redis, packKey);
                if (other != null) return (Remember(stateId, version, other), false);
            }
        }

        try
        {
            byte[]? raw;
            try
            {
                var atlas = sp.GetRequiredService<IAtlasClient>();
                if (!atlas.IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");
                raw = await atlas.GetMapPackRawAsync(stateId);
            }
            catch (Exception ex)
            {
                logger.LogWarning("TripMapPackService: Atlas pack {StateId} unavailable: {Error}", stateId, ex.Message);
                return (null, true);
            }

            if (raw == null)
            {
                await ForceRefreshAsync(CancellationToken.None);
                return (null, false);
            }
            if (raw.Length > TripMapPackKeys.MaxRawBytes)
            {
                logger.LogError("mappack.oversize {StateId} {Bytes}", stateId, raw.Length);
                return (null, true);
            }
            if (VersionOf(raw) != version)
            {
                await ForceRefreshAsync(CancellationToken.None);
                return (null, false);
            }

            var gzip = Gzip(raw);
            await WriteAsync(redis, packKey, gzip, TripMapPackKeys.PackTtl);
            return (Remember(stateId, version, gzip), false);
        }
        finally
        {
            if (acquired == true) await DeleteAsync(redis, lockKey);
        }
    }

    // One current entry per state: a newer version replaces the older one.
    private TripMapPackBytes Remember(Guid stateId, string version, byte[] gzip)
    {
        var entry = new TripMapPackBytes(version, gzip);
        _bytes[stateId] = entry;
        return entry;
    }

    internal static string? VersionOf(byte[] raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(raw);
        return output.ToArray();
    }

    public static byte[] Gunzip(byte[] gzip)
    {
        using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private async Task<TripMapPackIndex?> ReadIndexAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(TripMapPackKeys.Index);
            if (value.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<TripMapPackIndex>(value.ToString(), TripJson.Options);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: read of {Key} failed: {Error}", TripMapPackKeys.Index, ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> ReadBytesAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : (byte[]?)value;
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: read of {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private async Task WriteAsync(IConnectionMultiplexer? redis, string key, RedisValue value, TimeSpan? ttl = null)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(key, value, ttl ?? TripMapPackKeys.Ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: write of {Key} failed: {Error}", key, ex.Message);
        }
    }

    // null = no Redis or Redis failed (no cross-process lock, load directly).
    private async Task<bool?> TryAcquireLockAsync(IConnectionMultiplexer? redis, string key, TimeSpan ttl)
    {
        if (redis == null) return null;
        try
        {
            return await redis.GetDatabase().StringSetAsync(key, "1", ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: lock {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private async Task DeleteAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripMapPackService: delete of {Key} failed: {Error}", key, ex.Message);
        }
    }
}

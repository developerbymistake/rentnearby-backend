using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

internal interface ITaxiPickupCacheStore
{
    Task<long> GenerationAsync();
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan ttl);
    Task BumpAsync();
}

// Redis behind a short timeout and a pause after a failure, so a missing or slow Redis costs one slow call, not one per request.
internal sealed class RedisPickupCacheStore(IConnectionMultiplexer redis) : ITaxiPickupCacheStore
{
    public const string GenerationKey = "taxi:pickup:gen";
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(10);

    private static long _pausedUntilTicks;

    public async Task<long> GenerationAsync()
    {
        var value = await RunAsync(db => db.StringGetAsync(GenerationKey));
        return value.HasValue && long.TryParse((string?)value, out var gen) ? gen : 0;
    }

    public async Task<string?> GetAsync(string key) => (string?)await RunAsync(db => db.StringGetAsync(key));

    public async Task SetAsync(string key, string value, TimeSpan ttl) => await RunAsync(db => db.StringSetAsync(key, value, ttl));

    public async Task BumpAsync() => await RunAsync(db => db.StringIncrementAsync(GenerationKey));

    private async Task<T> RunAsync<T>(Func<IDatabase, Task<T>> call)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _pausedUntilTicks))
            throw new InvalidOperationException("Redis is paused for the pickup cache");
        try
        {
            if (!redis.IsConnected) throw new InvalidOperationException("Redis is not connected");
            return await call(redis.GetDatabase()).WaitAsync(CallTimeout);
        }
        catch
        {
            Interlocked.Exchange(ref _pausedUntilTicks, Environment.TickCount64 + (long)PauseAfterFailure.TotalMilliseconds);
            throw;
        }
    }
}

// 60 second cache of the pickup queries per city key. A bump moves the generation, so every older entry is ignored at once.
// Any cache failure (no store, Redis down, slow, bad entry) means the database answers: the cache never decides a result.
internal sealed class TaxiPickupCache(ITaxiPickupCacheStore? store, ILogger? logger = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string KeyOf(string kind, long generation, long cityKey) => $"taxi:pickup:{kind}:{generation}:{cityKey}";

    public async Task<T> GetOrLoadAsync<T>(string kind, long cityKey, Func<Task<T>> load)
    {
        if (store is null) return await load();

        string? key = null;
        try
        {
            key = KeyOf(kind, await store.GenerationAsync(), cityKey);
            if (await store.GetAsync(key) is { } hit && JsonSerializer.Deserialize<T>(hit, Json) is { } value) return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Pickup cache read failed, using the database");
            key = null;
        }

        var loaded = await load();
        if (key is not null)
        {
            try
            {
                await store.SetAsync(key, JsonSerializer.Serialize(loaded, Json), Ttl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Pickup cache write failed");
            }
        }
        return loaded;
    }

    public async Task BumpAsync()
    {
        if (store is null) return;
        try
        {
            await store.BumpAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Pickup cache bump failed; entries expire within {Seconds} s", Ttl.TotalSeconds);
        }
    }
}

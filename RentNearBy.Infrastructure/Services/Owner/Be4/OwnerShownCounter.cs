using Microsoft.Extensions.Logging;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// AU-8 "Shown": +1 per stay per trip stay list / stay detail response seen by someone other than the owner. One fire-and-forget
// HINCRBY pipeline per response (key TTL 3 days); never awaited on the response path and never throws. No Redis = nothing counted.
public sealed class OwnerShownCounter(TimeProvider clock, ILogger<OwnerShownCounter> logger, IConnectionMultiplexer? redis = null)
{
    public const string KeyPrefix = "owner:shown:";
    public static readonly TimeSpan KeyTtl = TimeSpan.FromDays(3);

    public static string Key(DateOnly istDay) => KeyPrefix + istDay.ToString("yyyyMMdd");

    // ownerByStay: stay id -> owner user id; the caller's own stays are skipped (viewer null = anonymous, counted).
    public void Add(IEnumerable<(Guid StayId, Guid OwnerUserId)> stays, Guid? viewerUserId)
    {
        try
        {
            if (redis is null || !redis.IsConnected) return;
            var ids = stays.Where(s => viewerUserId is null || s.OwnerUserId != viewerUserId.Value)
                .Select(s => s.StayId).Distinct().ToList();
            if (ids.Count == 0) return;
            var key = Key(IstTime.TodayIst(clock.GetUtcNow().UtcDateTime));
            var dbx = redis.GetDatabase();
            var batch = dbx.CreateBatch();
            foreach (var id in ids)
                _ = batch.HashIncrementAsync(key, id.ToString("N"), 1, CommandFlags.FireAndForget);
            _ = batch.KeyExpireAsync(key, KeyTtl, CommandFlags.FireAndForget);
            batch.Execute();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "owner.shown counter skipped");
        }
    }

    public void Add(Guid stayId, Guid ownerUserId, Guid? viewerUserId) => Add([(stayId, ownerUserId)], viewerUserId);

    // FB-53 (D-31): a detail view counts once per viewer (user id, or the client address when anonymous) per stay per IST day:
    // one script SETs the seen key NX for a day and increments only when it was new. Fire-and-forget, never throws.
    public const string SeenPrefix = "owner:shown:seen:";
    public static readonly TimeSpan SeenTtl = TimeSpan.FromDays(1);
    public const int ListedCounted = 20;

    public static string SeenKey(DateOnly istDay, Guid stayId, string viewer) => $"{SeenPrefix}{istDay:yyyyMMdd}:{stayId:N}:{viewer}";

    internal const string OncePerDayScript = """
        if redis.call('SET', KEYS[1], '1', 'NX', 'EX', ARGV[2]) then
          redis.call('HINCRBY', KEYS[2], ARGV[1], 1)
          redis.call('EXPIRE', KEYS[2], ARGV[3])
        end
        return 0
        """;

    public void AddViewed(Guid stayId, Guid ownerUserId, Guid? viewerUserId, string? viewerAddress)
    {
        try
        {
            if (redis is null || !redis.IsConnected) return;
            if (viewerUserId is { } v && v == ownerUserId) return;
            var viewer = viewerUserId?.ToString("N") ?? ("ip:" + (string.IsNullOrWhiteSpace(viewerAddress) ? "unknown" : viewerAddress));
            var day = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
            _ = redis.GetDatabase().ScriptEvaluateAsync(OncePerDayScript,
                [SeenKey(day, stayId, viewer), Key(day)],
                [stayId.ToString("N"), (long)SeenTtl.TotalSeconds, (long)KeyTtl.TotalSeconds],
                CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "owner.shown counter skipped");
        }
    }

    // The trip stay list rows carry no owner id; the caller skips the list for a Stay Owner viewer (his own stay could be in it).
    // FB-53: only the first 20 rows count (what a phone shows), so one city list costs at most 21 commands in one pipeline.
    public void AddListed(IEnumerable<Guid> stayIds) => Add(stayIds.Take(ListedCounted).Select(id => (id, Guid.Empty)), null);
}

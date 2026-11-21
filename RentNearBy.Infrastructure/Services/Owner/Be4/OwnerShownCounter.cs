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

    // The trip stay list rows carry no owner id; the caller skips the list for a Stay Owner viewer (his own stay could be in it).
    public void AddListed(IEnumerable<Guid> stayIds) => Add(stayIds.Select(id => (id, Guid.Empty)), null);
}

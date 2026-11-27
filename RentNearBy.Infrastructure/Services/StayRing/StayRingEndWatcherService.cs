using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.StayRing;

public sealed record LiveStayRing(string Key, Guid EnquiryId, Guid OwnerUserId);

// Use cases 7 and 8: a ring ends on every device when its enquiry leaves Asked anywhere (the owner answered from Enquiries on any
// device, the traveller withdrew, the trip closed it, it expired). A live ring is a pending missed row (due at the ring's expiry),
// found through ix_outbox_due; every enquiry path stays untouched. One pass cancels the missed row and writes ONE ring end
// (the conditional UPDATE decides the single winner).
public static class StayRingEndPass
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal const string LiveRingsSql = """
        SELECT "Key", "Payload" FROM "OutboxMessages"
        WHERE "PublishedAt" IS NULL AND "NextAttemptAt" > now() AND "NextAttemptAt" <= now() + make_interval(secs => @window)
          AND "Key" LIKE 'stayringmiss:%'
        ORDER BY "NextAttemptAt", "Id" LIMIT @take
        """;

    internal sealed class Row
    {
        public string Key { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    public static IReadOnlyList<LiveStayRing> Parse(IEnumerable<(string Key, string Payload)> rows)
    {
        var live = new List<LiveStayRing>();
        foreach (var (key, payload) in rows)
        {
            if (!StayRingPushKinds.TryParseMissedKey(key, out var enquiryId)) continue;
            OutboxPushPayload? push;
            try { push = JsonSerializer.Deserialize<OutboxPushPayload>(payload, Json); }
            catch (JsonException) { continue; }
            if (push is null || push.UserId == Guid.Empty) continue;
            live.Add(new LiveStayRing(key, enquiryId, push.UserId));
        }
        return live;
    }

    // The rings to end and why: an enquiry that is gone counts as closed.
    public static IReadOnlyList<(LiveStayRing Ring, string Reason)> Ended(IReadOnlyList<LiveStayRing> live, IReadOnlyDictionary<Guid, string> statusById)
        => live.Select(r => (Ring: r, Reason: statusById.TryGetValue(r.EnquiryId, out var status) ? StayRingRules.EndReasonOf(status) : StayRingEndReasons.Closed))
            .Where(x => x.Reason != null).Select(x => (x.Ring, x.Reason!)).ToList();

    public static async Task<int> RunOnceAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(LiveRingsSql,
            new NpgsqlParameter("window", NpgsqlDbType.Integer) { Value = StayRingRules.RingSeconds + 60 },
            new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = StayRingRules.WatchBatch }).ToListAsync(ct);
        var live = Parse(rows.Select(r => (r.Key, r.Payload)));
        if (live.Count == 0) return 0;

        var ids = live.Select(r => r.EnquiryId).Distinct().ToList();
        var statuses = await db.TripEnquiries.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Status }).ToDictionaryAsync(e => e.Id, e => e.Status, ct);
        var ended = Ended(live, statuses);
        if (ended.Count == 0) return 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var won = (await StayRingWriter.CancelMissedAsync(db, ended.Select(x => x.Ring.Key).ToList(), ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (ring, reason) in ended.Where(x => won.Contains(x.Ring.Key)))
            StayRingWriter.AddRingEnd(db, StayRingPushKinds.RingEndKey(ring.EnquiryId), ring.EnquiryId, ring.OwnerUserId, reason, nowUtc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return won.Count;
    }
}

// Every 5 s; two indexed reads when nothing rings.
public sealed class StayRingEndWatcherService(IServiceScopeFactory scopeFactory, IOutboxSignal outbox, ILogger<StayRingEndWatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StayRingRules.WatchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (await StayRingEndPass.RunOnceAsync(db, DateTime.UtcNow, ct) > 0) outbox.Wake();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Stay ring end pass failed");
        }
    }
}

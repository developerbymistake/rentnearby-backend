using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// F1 / D11: the evening-before alert (18:00 IST) for EVERY owner, Free included, RemindersOn ignored. One push per app booking that
// starts tomorrow (Auto, not cancelled); the outbox key is the send-once key, so a rerun the same evening writes nothing new.
// Per batch of listings (keyset by Id) one short transaction: one select, one outbox insert. Nothing runs in quiet hours (22-07 IST).
public abstract class OwnerEveAlertJob(
    ApplicationDbContext db,
    IOwnerNotifier notifier,
    IOptions<OwnerFeatureOptions> options,
    TimeProvider clock,
    ILogger logger) : IBuddyFeatureJob
{
    public const int QuietFrom = 22;
    public const int QuietTo = 7;
    public const int LateHour = 21;

    protected abstract string Kind { get; }

    protected abstract OwnerPush Build(Guid userId, EveLine line, DateOnly tomorrow);

    public abstract string Name { get; }

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public virtual TimeOnly? DailyAtIst => ParseIstTime(options.Value.EveAlertIst) ?? new TimeOnly(IstTime.EveHour, 0);

    public string BatchSql => $"""
        SELECT l."Id" AS "ListingId", l."UserId",
               (SELECT json_agg(json_build_object('id', e."Id", 'label', e."Label", 'detail', e."Detail", 'from', e."FromPlace") ORDER BY e."Id")
                FROM "CalendarEntries" e
                WHERE e."ListingKind" = '{Kind}' AND e."ListingId" = l."Id" AND e."Kind" = '{CalendarEntryKinds.Auto}'
                  AND NOT e."Cancelled" AND e."DateFrom" = @tomorrow)::text AS "Lines"
        FROM {Table(Kind)} l
        JOIN "Users" u ON u."Id" = l."UserId" AND u."IsActive"
        WHERE l."Id" > @after AND l."Status" = 'Approved' AND NOT l."IsDeleted"
          AND {HasValidToken("l")}
        ORDER BY l."Id"
        LIMIT @n
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (IstTime.IsQuiet(now, QuietFrom, QuietTo))
        {
            logger.LogInformation("owner.eve {Kind} skipped: quiet hours", Kind);
            return 0;
        }

        var tomorrow = IstTime.TodayIst(now).AddDays(1);
        var sql = BatchSql;
        var batch = Math.Clamp(options.Value.JobBatch, 1, 5000);
        var after = Guid.Empty;
        var sent = 0;
        while (true) // batch-loop
        {
            var cursor = after;
            var (rows, written) = await PgRetry.OnceAsync(db, async c =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(c);
                var listings = await db.Database.SqlQueryRaw<ListingRow>(sql, Uuid("after", cursor), Int("n", batch), Date("tomorrow", tomorrow)).ToListAsync(c);
                var pushes = Plan(listings, tomorrow);
                await notifier.PushAsync(pushes, c);
                await tx.CommitAsync(c);
                return (listings, pushes.Count);
            }, ct, logger);
            sent += written;
            if (rows.Count < batch) break;
            after = rows[^1].ListingId;
        }
        if (sent > 0) notifier.Wake();
        return sent;
    }

    // Pure: one push per booking that starts tomorrow.
    public IReadOnlyList<OwnerPush> Plan(IReadOnlyList<ListingRow> listings, DateOnly tomorrow)
        => listings.SelectMany(l => ParseLines(l.Lines).Select(line => Build(l.UserId, line, tomorrow))).ToList();

    public static IReadOnlyList<EveLine> ParseLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var lines = new List<EveLine>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !Guid.TryParse(Str(e, "id"), out var id)) continue;
                lines.Add(new EveLine(id, Str(e, "label"), Str(e, "detail"), Str(e, "from")));
            }
            return lines;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public sealed record EveLine(Guid EntryId, string? Label, string? Detail, string? FromPlace);

    public sealed class ListingRow
    {
        public Guid ListingId { get; set; }
        public Guid UserId { get; set; }
        public string? Lines { get; set; }
    }
}

public sealed class StayEveAlertJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<StayEveAlertJob> logger)
    : OwnerEveAlertJob(db, notifier, options, clock, logger)
{
    public override string Name => "owner.eve.stay";

    protected override string Kind => ListingKinds.Stay;

    protected override OwnerPush Build(Guid userId, EveLine line, DateOnly tomorrow)
        => StayOwnerPushCopy.CheckInTomorrow(userId, line.EntryId, tomorrow, line.Label, line.Detail);
}

public sealed class TaxiEveAlertJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<TaxiEveAlertJob> logger)
    : OwnerEveAlertJob(db, notifier, options, clock, logger)
{
    public override string Name => "owner.eve.taxi";

    protected override string Kind => ListingKinds.Taxi;

    protected override OwnerPush Build(Guid userId, EveLine line, DateOnly tomorrow)
        => TaxiDriverPushCopy.TripTomorrow(userId, line.EntryId, tomorrow, line.FromPlace);
}

// Second pass at 21:00 IST for bookings made after the 18:00 run; the send-once key drops the ones already sent.
public sealed class StayEveLateAlertJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<StayEveAlertJob> logger)
    : OwnerEveAlertJob(db, notifier, options, clock, logger)
{
    public override string Name => "owner.eve.stay.late";

    public override TimeOnly? DailyAtIst => new TimeOnly(LateHour, 0);

    protected override string Kind => ListingKinds.Stay;

    protected override OwnerPush Build(Guid userId, EveLine line, DateOnly tomorrow)
        => StayOwnerPushCopy.CheckInTomorrow(userId, line.EntryId, tomorrow, line.Label, line.Detail);
}

public sealed class TaxiEveLateAlertJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<TaxiEveAlertJob> logger)
    : OwnerEveAlertJob(db, notifier, options, clock, logger)
{
    public override string Name => "owner.eve.taxi.late";

    public override TimeOnly? DailyAtIst => new TimeOnly(LateHour, 0);

    protected override string Kind => ListingKinds.Taxi;

    protected override OwnerPush Build(Guid userId, EveLine line, DateOnly tomorrow)
        => TaxiDriverPushCopy.TripTomorrow(userId, line.EntryId, tomorrow, line.FromPlace);
}

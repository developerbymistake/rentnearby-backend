using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public sealed class ReminderRow
{
    public Guid UserId { get; set; }
    public Guid Id { get; set; }
    public Guid? TripId { get; set; }
    public string? StepKey { get; set; }
    public string? Name { get; set; }
    public DateTime? At { get; set; }
    public DateOnly? On { get; set; }
    public string? Kind { get; set; }
    // The owner already wrote a real chat message after the enquiry was made.
    public bool HostChatted { get; set; }
    // The enquiry holds an offer that lapsed unanswered.
    public bool OfferLapsed { get; set; }
}

public enum ReminderCategory { Final, Shortlist, NoReply, Host }

// One push for one person, plus the rows the push claims (set in the same transaction as the outbox row).
public sealed record ReminderPlan(
    Guid UserId, ReminderCategory Category, OutboxPushPayload Push, IReadOnlyList<Guid> ShortlistIds, IReadOnlyList<Guid> EnquiryIds);

// Pure part of the morning run: rows of one user chunk in, at most one push per person out (priority final, shortlist, no reply).
public static class TripReminderPlanner
{
    public static IReadOnlyList<ReminderPlan> PlanTravellers(
        DateOnly today, IEnumerable<ReminderRow> finalShortlist, IEnumerable<ReminderRow> finalEnquiries,
        IEnumerable<ReminderRow> activeShortlist, IEnumerable<ReminderRow> enquiriesOut)
    {
        var fs = finalShortlist.ToLookup(r => r.UserId);
        var fe = finalEnquiries.ToLookup(r => r.UserId);
        var act = activeShortlist.ToLookup(r => r.UserId);
        var outs = enquiriesOut.ToLookup(r => r.UserId);
        var users = fs.Select(g => g.Key).Concat(fe.Select(g => g.Key)).Concat(act.Select(g => g.Key)).Concat(outs.Select(g => g.Key)).Distinct().Order();

        var plans = new List<ReminderPlan>();
        foreach (var user in users)
        {
            var key = TripReminderRules.TravellerKey(user, today);
            if (fs[user].Any() || fe[user].Any())
            {
                // A row tied to a step opens that step; a step-less enquiry opens the trip plan with its own generic line.
                var all = fs[user].Concat(fe[user]).ToList();
                var withStep = all.Where(r => !string.IsNullOrEmpty(r.StepKey)).ToList();
                var rep = First(withStep.Count > 0 ? withStep : all);
                var copy = rep.OfferLapsed ? TripReminderPush.OfferEnded(rep.Name)
                    : string.IsNullOrEmpty(rep.StepKey) ? TripReminderPush.EnquiryEnded(rep.Name)
                    : TripReminderPush.ShortlistEnded();
                plans.Add(Plan(user, key, ReminderCategory.Final, copy, rep, nudge: false, reopen: true,
                    fs[user].Select(r => r.Id), fe[user].Select(r => r.Id)));
            }
            else if (act[user].Any())
            {
                var rows = act[user].ToList();
                plans.Add(Plan(user, key, ReminderCategory.Shortlist, TripReminderPush.ShortlistWaiting(First(rows).Name, rows.Count), First(rows),
                    nudge: true, reopen: false, rows.Select(r => r.Id), []));
            }
            else if (outs[user].Any())
            {
                var rows = outs[user].ToList();
                // A host who wrote in chat is not "no reply": the no-reply line counts only the silent ones.
                var silent = rows.Where(r => !r.HostChatted).ToList();
                var copy = silent.Count > 0
                    ? TripReminderPush.NoReplyYet(First(silent).Name, silent.Count)
                    : TripReminderPush.HostChatted(First(rows).Name, rows.Count);
                var lead = silent.Count > 0 ? First(silent) : First(rows);
                plans.Add(Plan(user, key, ReminderCategory.NoReply, copy, lead, nudge: silent.Count > 0, reopen: false, [], []));
            }
        }
        return plans;
    }

    public static IReadOnlyList<ReminderPlan> PlanHosts(DateOnly today, IEnumerable<ReminderRow> waiting)
    {
        var plans = new List<ReminderPlan>();
        foreach (var group in waiting.GroupBy(r => r.UserId).OrderBy(g => g.Key))
        {
            var rows = group.OrderBy(r => r.On ?? DateOnly.MaxValue).ThenBy(r => r.Id).ToList();
            var first = rows[0];
            var copy = TripReminderPush.HostWaiting(rows.Count, first.Name, first.On);
            var taxi = string.Equals(first.Kind, TripEnquiryKinds.Taxi, StringComparison.Ordinal);
            var route = taxi ? UserNotificationRoutes.TripLeads : UserNotificationRoutes.StayEnquiries;
            var args = rows.Count == 1 ? JsonSerializer.Serialize(new { enquiryId = first.Id }) : "{}";
            var key = TripReminderRules.HostKey(group.Key, today);
            plans.Add(new ReminderPlan(group.Key, ReminderCategory.Host,
                new OutboxPushPayload(key, group.Key, null, copy.Title, copy.Body, route, args, key), [], rows.Select(r => r.Id).ToList()));
        }
        return plans;
    }

    private static ReminderRow First(IEnumerable<ReminderRow> rows) => rows.OrderBy(r => r.At ?? DateTime.MaxValue).ThenBy(r => r.Id).First();

    private static ReminderPlan Plan(
        Guid user, string key, ReminderCategory category, TripReminderCopy copy, ReminderRow first, bool nudge, bool reopen,
        IEnumerable<Guid> shortlistIds, IEnumerable<Guid> enquiryIds)
        => new(user, category,
            new OutboxPushPayload(key, user, null, copy.Title, copy.Body, UserNotificationRoutes.TripPlan,
                TripReminderPush.TravellerArgs(first.TripId ?? Guid.Empty, first.StepKey, nudge, reopen, first.Name), key),
            shortlistIds.ToList(), enquiryIds.ToList());
}

// The 3-day clock. One run per IST date, set-based and keyset chunked by user (2000 users a chunk, nothing held between chunks).
// A row is claimed (RemindedOn / FinalPushedAt) only when its person's outbox row was inserted in the same transaction, and the unique
// outbox Key makes a second run the same day a no-op, so a restart or a second instance never double-sends.
public sealed class TripReminderRunner(ILogger logger)
{
    public const int ChunkUsers = 2000;

    private const string TripOk = """t."MutedAt" IS NULL AND t."Status" <> 'Completed' AND (t."StartDate" IS NULL OR t."StartDate" > {0})""";
    // Mute silences the traveller's reminders only: a host with an unanswered enquiry is still reminded (hence no MutedAt here).
    // Hosts are reminded about trip-less enquiries too (LEFT JOIN Trips): no trip row means nothing to stop on.
    private const string HostTripOk = """(t."Id" IS NULL OR (t."Status" <> 'Completed' AND (t."StartDate" IS NULL OR t."StartDate" > {0})))""";
    private const string HostFrom = """FROM "TripEnquiries" e LEFT JOIN "Trips" t ON t."Id" = e."TripId" """;
    // A step with a confirmed or booked enquiry is settled: no reminder and no final push, whatever its other rows say.
    private const string StepSettled = """NOT EXISTS (SELECT 1 FROM "TripEnquiries" c WHERE c."TripId" = {0} AND c."StepKey" = {1} AND c."Status" IN ('ConfirmedByBoth','BookedByTraveller'))""";
    // A step with an enquiry still being answered needs neither a "send your enquiry" nudge nor a "shortlist ended" push.
    private const string StepOpen = """NOT EXISTS (SELECT 1 FROM "TripEnquiries" o WHERE o."TripId" = {0} AND o."StepKey" = {1} AND o."Status" IN ('Asked','Offered','Countered','FareSent'))""";
    // The shortlisted listing must still exist and be bookable (the rules of GET shortlist): a stay is Approved and accepting guests, a taxi
    // is Approved and available, and a local taxi also needs a day rate. A removed host never gets a "send your enquiry" nudge.
    private const string ShortlistListingLive =
        """(s."Kind" = 'Stay' AND EXISTS (SELECT 1 FROM "StayListings" sl WHERE sl."Id" = s."ListingId" AND sl."IsDeleted" = false AND sl."Status" = 'Approved' AND sl."AcceptingGuests" = true))""" +
        """ OR (s."Kind" IN ('Taxi', 'Car') AND EXISTS (SELECT 1 FROM "TaxiListings" tl WHERE tl."Id" = s."ListingId" AND tl."IsDeleted" = false AND tl."Status" = 'Approved' AND tl."IsAvailable" = true AND (s."Kind" = 'Car' OR tl."LocalDayRate" IS NOT NULL)))""";
    private static readonly string ShortlistSettled = StepSettled.Replace("{0}", "s.\"TripId\"").Replace("{1}", "s.\"StepKey\"");
    private static readonly string EnquirySettled = StepSettled.Replace("{0}", "e.\"TripId\"").Replace("{1}", "e.\"StepKey\"");
    private static readonly string ShortlistOpen = StepOpen.Replace("{0}", "s.\"TripId\"").Replace("{1}", "s.\"StepKey\"");
    // The host's offer lapsed unanswered: the host did answer, so nobody is "waiting" and nobody is "unanswered".
    private const string NoLapsedOffer = """NOT EXISTS (SELECT 1 FROM "TripOffers" lo WHERE lo."TripEnquiryId" = e."Id" AND lo."Status" = 'Expired')""";
    private const string HasLapsedOffer = """EXISTS (SELECT 1 FROM "TripOffers" lo WHERE lo."TripEnquiryId" = e."Id" AND lo."Status" = 'Expired')""";
    // The owner already wrote a real (non-system) chat message after the enquiry was made.
    private const string OwnerChatted = """EXISTS (SELECT 1 FROM "Messages" m WHERE m."ConversationId" = e."ConversationId" AND m."SenderId" = e."OwnerUserId" AND m."Type" <> 'system' AND m."CreatedAt" > e."CreatedAt")""";
    private static readonly string FinalWindow = "interval '" + TripReminderRules.FinalWindowDays + " days'";
    private static readonly string ShortlistFinal = """s."State" = 'Closed' AND s."ClosedReason" = 'NoReply' AND s."FinalPushedAt" IS NULL AND s."ClosedAt" >= {1} - """ + FinalWindow + " AND " + ShortlistSettled + " AND " + ShortlistOpen + " AND (" + ShortlistListingLive + ")";
    private static readonly string ShortlistActive = """s."State" = 'Active' AND s."ClosesAt" > {1} AND s."AddedAt" < {2} AND (s."RemindedOn" IS NULL OR s."RemindedOn" < {0}) AND """ + ShortlistSettled + " AND " + ShortlistOpen + " AND (" + ShortlistListingLive + ")";
    // Only an enquiry that ran out its full clock: one cut short by the check-in day expires earlier than CreatedAt + EnquiryHours.
    // A row without a StepKey is included (generic line that opens the trip plan); ix_tripenquiries_final stays StepKey-bound, so those rows are not served by it.
    private static readonly string EnquiryFinal = """e."Status" = 'Expired' AND e."FinalPushedAt" IS NULL AND e."UpdatedAt" >= {1} - """ + FinalWindow + """ AND e."ExpiresAt" >= e."CreatedAt" + interval '""" + TripExecutionRules.EnquiryHours + """ hours' AND """ + EnquirySettled;
    private static readonly string EnquiryOut = """e."Status" = 'Asked' AND e."ExpiresAt" > {1} AND e."CreatedAt" < {2} AND """ + NoLapsedOffer;
    private static readonly string EnquiryHostWaiting = """e."Status" IN ('Asked', 'Countered') AND e."ExpiresAt" > {1} AND e."CreatedAt" < {2} - interval '""" + TripReminderRules.HostMinAgeHours + """ hours' AND (e."RemindedOn" IS NULL OR e."RemindedOn" < {0}) AND """
        + NoLapsedOffer + " AND NOT " + OwnerChatted + " AND " + EnquirySettled;

    private const string ShortlistCols = """s."TravellerUserId" AS "UserId", s."Id" AS "Id", s."TripId" AS "TripId", s."StepKey" AS "StepKey", s."ListingName" AS "Name", COALESCE(s."ClosedAt", s."AddedAt") AS "At", NULL::date AS "On", s."Kind" AS "Kind", false AS "HostChatted", false AS "OfferLapsed" """;
    private static readonly string EnquiryCols = """e."TravellerUserId" AS "UserId", e."Id" AS "Id", e."TripId" AS "TripId", e."StepKey" AS "StepKey", e."ListingName" AS "Name", e."CreatedAt" AS "At", NULL::date AS "On", e."Kind" AS "Kind", """ + OwnerChatted + """ AS "HostChatted", """ + HasLapsedOffer + """ AS "OfferLapsed" """;
    private const string HostCols = """e."OwnerUserId" AS "UserId", e."Id" AS "Id", e."TripId" AS "TripId", e."StepKey" AS "StepKey", COALESCE(u."Name", '') AS "Name", e."CreatedAt" AS "At", COALESCE(e."CheckIn", t."StartDate") AS "On", e."Kind" AS "Kind", false AS "HostChatted", false AS "OfferLapsed" """;

    public async Task<int> RunAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var today = IstTime.TodayIst(nowUtc);
        var runTime = TripReminderRules.MorningDueUtc(today);
        var sent = 0;

        var after = Guid.Empty;
        while (!ct.IsCancellationRequested)
        {
            var users = await db.Database.SqlQueryRaw<Guid>($$"""
                SELECT u AS "Value" FROM (
                  (SELECT DISTINCT s."TravellerUserId" AS u FROM "TripShortlistItems" s JOIN "Trips" t ON t."Id" = s."TripId"
                   WHERE s."TravellerUserId" > {3} AND {{TripOk}} AND (({{ShortlistFinal}}) OR ({{ShortlistActive}})) ORDER BY 1 LIMIT {4})
                  UNION
                  (SELECT DISTINCT e."TravellerUserId" AS u FROM "TripEnquiries" e JOIN "Trips" t ON t."Id" = e."TripId"
                   WHERE e."TravellerUserId" > {3} AND {{TripOk}} AND (({{EnquiryFinal}}) OR ({{EnquiryOut}})) ORDER BY 1 LIMIT {4})
                ) x ORDER BY u LIMIT {4}
                """, today, nowUtc, runTime, after, ChunkUsers).ToListAsync(ct);
            if (users.Count == 0) break;
            after = users[^1];
            var ids = users.ToArray();

            var finalS = await Rows($$"""SELECT {{ShortlistCols}} FROM "TripShortlistItems" s JOIN "Trips" t ON t."Id" = s."TripId" WHERE s."TravellerUserId" = ANY({5}) AND {{TripOk}} AND {{ShortlistFinal}}""", db, today, nowUtc, runTime, after, ChunkUsers, ids, ct);
            var finalE = await Rows($$"""SELECT {{EnquiryCols}} FROM "TripEnquiries" e JOIN "Trips" t ON t."Id" = e."TripId" WHERE e."TravellerUserId" = ANY({5}) AND {{TripOk}} AND {{EnquiryFinal}}""", db, today, nowUtc, runTime, after, ChunkUsers, ids, ct);
            var active = await Rows($$"""SELECT {{ShortlistCols}} FROM "TripShortlistItems" s JOIN "Trips" t ON t."Id" = s."TripId" WHERE s."TravellerUserId" = ANY({5}) AND {{TripOk}} AND {{ShortlistActive}}""", db, today, nowUtc, runTime, after, ChunkUsers, ids, ct);
            var outs = await Rows($$"""SELECT {{EnquiryCols}} FROM "TripEnquiries" e JOIN "Trips" t ON t."Id" = e."TripId" WHERE e."TravellerUserId" = ANY({5}) AND {{TripOk}} AND {{EnquiryOut}}""", db, today, nowUtc, runTime, after, ChunkUsers, ids, ct);

            sent += await CommitAsync(db, TripReminderPlanner.PlanTravellers(today, finalS, finalE, active, outs), today, nowUtc, ct);
        }

        after = Guid.Empty;
        while (!ct.IsCancellationRequested)
        {
            var owners = await db.Database.SqlQueryRaw<Guid>($$"""
                SELECT DISTINCT e."OwnerUserId" AS "Value" {{HostFrom}}
                WHERE e."OwnerUserId" > {3} AND {{HostTripOk}} AND {{EnquiryHostWaiting}} ORDER BY 1 LIMIT {4}
                """, today, nowUtc, runTime, after, ChunkUsers).ToListAsync(ct);
            if (owners.Count == 0) break;
            after = owners[^1];
            var ids = owners.ToArray();
            var waiting = await Rows($$"""SELECT {{HostCols}} {{HostFrom}} LEFT JOIN "Users" u ON u."Id" = e."TravellerUserId" WHERE e."OwnerUserId" = ANY({5}) AND {{HostTripOk}} AND {{EnquiryHostWaiting}}""", db, today, nowUtc, runTime, after, ChunkUsers, ids, ct);
            sent += await CommitAsync(db, TripReminderPlanner.PlanHosts(today, waiting), today, nowUtc, ct);
        }
        return sent;
    }

    private static Task<List<ReminderRow>> Rows(
        string sql, ApplicationDbContext db, DateOnly today, DateTime now, DateTime runTime, Guid after, int chunk, Guid[] ids, CancellationToken ct)
        => db.Database.SqlQueryRaw<ReminderRow>(sql, today, now, runTime, after, chunk, ids).ToListAsync(ct);

    private async Task<int> CommitAsync(ApplicationDbContext db, IReadOnlyList<ReminderPlan> plans, DateOnly today, DateTime now, CancellationToken ct)
    {
        if (plans.Count == 0) return 0;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await InsertOutboxAsync(db, tx, plans, json, now, ct);
        var claimed = plans.Where(p => inserted.Contains(p.Push.Key)).ToList();

        var finalShortlist = Ids(claimed, ReminderCategory.Final, p => p.ShortlistIds);
        var finalEnquiries = Ids(claimed, ReminderCategory.Final, p => p.EnquiryIds);
        var shortlist = Ids(claimed, ReminderCategory.Shortlist, p => p.ShortlistIds);
        var hosts = Ids(claimed, ReminderCategory.Host, p => p.EnquiryIds);
        if (finalShortlist.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "TripShortlistItems" SET "FinalPushedAt" = {now} WHERE "Id" = ANY({finalShortlist}) AND "FinalPushedAt" IS NULL""", ct);
        if (finalEnquiries.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "TripEnquiries" SET "FinalPushedAt" = {now} WHERE "Id" = ANY({finalEnquiries}) AND "FinalPushedAt" IS NULL""", ct);
        if (shortlist.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "TripShortlistItems" SET "RemindedOn" = {today} WHERE "Id" = ANY({shortlist})""", ct);
        if (hosts.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "TripEnquiries" SET "RemindedOn" = {today} WHERE "Id" = ANY({hosts})""", ct);
        await tx.CommitAsync(ct);

        if (claimed.Count < plans.Count)
            logger.LogInformation("Trip reminders: {Skipped} people already had today's push", plans.Count - claimed.Count);
        return claimed.Count;
    }

    private static Guid[] Ids(IEnumerable<ReminderPlan> plans, ReminderCategory category, Func<ReminderPlan, IReadOnlyList<Guid>> pick)
        => plans.Where(p => p.Category == category).SelectMany(pick).ToArray();

    private static async Task<HashSet<string>> InsertOutboxAsync(
        ApplicationDbContext db, IDbContextTransaction tx, IReadOnlyList<ReminderPlan> plans, JsonSerializerOptions json, DateTime now, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
            SELECT x.k, @exchange, @route, x.p, @now, @now, 0 FROM unnest(@keys, @payloads) AS x(k, p)
            ON CONFLICT ("Key") DO NOTHING RETURNING "Key"
            """, conn, (NpgsqlTransaction)tx.GetDbTransaction());
        cmd.Parameters.AddWithValue("exchange", OutboxTargets.PushExchange);
        cmd.Parameters.AddWithValue("route", OutboxTargets.PushRoutingKey);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        cmd.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = plans.Select(p => p.Push.Key).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = plans.Select(p => JsonSerializer.Serialize(p.Push, json)).ToArray() });
        var keys = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) keys.Add(reader.GetString(0));
        return keys;
    }
}

// Starts the run each morning at TripReminderRules IST time; catches up after a restart until 12:00 IST and never in the afternoon.
// A PostgreSQL advisory lock on a dedicated connection keeps a second instance from duplicating the work.
public sealed class TripReminderService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<TripExecutionOptions> options, ILogger<TripReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    public const string LockName = "trip.reminders";
    private DateOnly _doneDate, _alertedDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Trip reminder tick failed");
                }
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled) return false;
        var today = IstTime.TodayIst(nowUtc);
        if (_doneDate == today) return false;
        var overdue = TripReminderRules.RunMissed(nowUtc, todayFinished: false);
        if (!overdue && !TripReminderRules.ShouldRunMorning(nowUtc, todayFinished: false)) return false;
        if (overdue && _alertedDate == today) return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var finished = await db.Database.SqlQuery<int>(
            $"""SELECT COUNT(*)::int AS "Value" FROM "TripReminderRuns" WHERE "RunDate" = {today} AND "FinishedAt" IS NOT NULL""").SingleAsync(ct);
        if (finished > 0)
        {
            _doneDate = today;
            return false;
        }
        if (overdue)
        {
            // The catch-up window is over and no run finished today: nobody was reminded. Alert (see DEPLOY_CHECKLIST.md for the SQL check).
            _alertedDate = today;
            logger.LogCritical(new EventId(7002, "trip.reminders.missed"),
                "Trip reminders for {Date} never finished by 12:00 IST: no reminder push was queued today", today);
            return false;
        }

        var connectionString = db.Database.GetConnectionString() ?? throw new InvalidOperationException("No connection string");
        await using var gate = await PgAdvisoryLock.TryAcquireAsync(connectionString, LockName, ct);
        if (gate is null) return false;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TripReminderRuns" ("RunDate", "StartedAt", "Sent") VALUES ({today}, {nowUtc}, 0)
            ON CONFLICT ("RunDate") DO UPDATE SET "StartedAt" = {nowUtc} WHERE "TripReminderRuns"."FinishedAt" IS NULL
            """, ct);
        var sent = await new TripReminderRunner(logger).RunAsync(db, nowUtc, ct);
        if (ct.IsCancellationRequested) return false;
        var done = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "TripReminderRuns" SET "FinishedAt" = {done}, "Sent" = {sent} WHERE "RunDate" = {today}""", ct);
        _doneDate = today;
        logger.LogInformation("Trip reminders {Date}: {Sent} pushes queued", today, sent);
        return true;
    }
}

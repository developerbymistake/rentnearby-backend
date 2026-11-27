using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.StayRing;

// One new stay enquiry to ring: the row just added in the caller's transaction and the facts the ring shows.
public sealed record StayRingRequest(TripEnquiry Row, string? TravellerName, int? Adults, int? Kids, int? Rooms);

// The stay ring rows (contract F:/Dhasu/Buddy_resume/stay_ring/CONTRACT.md). Like TaxiPushWriter it only adds outbox rows to the
// caller's context and never saves or sends: the caller's transaction commits them with the enquiry, then wakes the relay.
// The missed ring is a second outbox row due at the ring's expiry (the relay only claims due rows): the watcher and the delivery
// check cancel it when the enquiry was answered or closed, the answer endpoint when the owner used the ring buttons.
public static class StayRingWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record Eligibility(Guid Id, Guid UserId, string Status, bool IsDeleted, bool EnquiryRingOn, Guid RoleId);

    // Use case 1 and 2: in the enquiry's transaction, one ring per new enquiry whose owner may be rung. Idempotent by key.
    public static async Task<int> StageRingsAsync(ApplicationDbContext db, IReadOnlyList<StayRingRequest> rings, DateTime nowUtc, CancellationToken ct = default)
    {
        var stayRings = rings.Where(r => r.Row.Kind == TripEnquiryKinds.Stay && r.Row.Status == TripEnquiryStatuses.Asked && r.Row.OwnerUserId != null).ToList();
        if (stayRings.Count == 0) return 0;
        var listingIds = stayRings.Select(r => r.Row.ListingId).Distinct().ToList();
        var stays = await db.StayListings.AsNoTracking().Where(s => listingIds.Contains(s.Id))
            .Select(s => new Eligibility(s.Id, s.UserId, s.Status, s.IsDeleted, s.EnquiryRingOn, s.User.RoleId))
            .ToDictionaryAsync(s => s.Id, ct);

        var staged = 0;
        foreach (var ring in stayRings)
        {
            if (!stays.TryGetValue(ring.Row.ListingId, out var stay) || stay.UserId != ring.Row.OwnerUserId) continue;
            if (!StayRingRules.CanRing(stay.RoleId, stay.Status, stay.IsDeleted, stay.EnquiryRingOn)) continue;
            AddRing(db, ring, stay.UserId, nowUtc);
            staged++;
        }
        return staged;
    }

    internal static void AddRing(ApplicationDbContext db, StayRingRequest ring, Guid ownerUserId, DateTime nowUtc)
    {
        var e = ring.Row;
        var snapshot = SnapshotOf(ring);
        var expiresAt = StayRingRules.RingExpiresAt(nowUtc, e.ExpiresAt);
        var data = StayRingPayload.Build(snapshot, expiresAt, nowUtc, ownerUserId);
        Add(db, new OutboxPushPayload(
            StayRingPushKinds.RingKey(e.Id), ownerUserId, null, StayRingPayload.RingTitle, data["line1"], data["action_route"], data["action_args_json"],
            StayRingPushKinds.RingTag(e.Id), 0, NotificationTypes.StayEnquiryRing, expiresAt, data), nowUtc, nowUtc);

        // Use case 9: a ring that ends by its window leaves a missed ring; an enquiry that ends inside the window needs none.
        if (expiresAt >= e.ExpiresAt) return;
        var missed = StayRingPayload.BuildMissed(snapshot, ownerUserId);
        Add(db, new OutboxPushPayload(
            StayRingPushKinds.MissedKey(e.Id), ownerUserId, null, missed.Title, missed.Body, missed.Data["action_route"], missed.Data["action_args_json"],
            StayRingPushKinds.RingTag(e.Id), 0, NotificationTypes.StayEnquiryRingMissed, e.ExpiresAt, missed.Data), nowUtc, expiresAt);
    }

    public static StayRingSnapshot SnapshotOf(StayRingRequest ring)
    {
        var e = ring.Row;
        return new StayRingSnapshot(e.Id, ring.TravellerName, e.ListingName, e.CheckIn, e.CheckOut, ring.Adults, ring.Kids, ring.Rooms,
            e.ListedStayTotal, TripOwnerBuckets.Of(e.Status));
    }

    // Visible ring end with the ring tag (the system replaces the ringing notification even when no app code runs). ExceptToken = the
    // device that answered; TargetTokens = a token copy when the caller deletes the tokens in the same unit (suspend, role change).
    public static void AddRingEnd(ApplicationDbContext db, string key, Guid enquiryId, Guid ownerUserId, string reason, DateTime nowUtc, string? exceptToken = null)
    {
        var parts = StayRingPayload.BuildEnd(enquiryId, reason, ownerUserId);
        Add(db, new OutboxPushPayload(
            key, ownerUserId, null, parts.Title, parts.Body, parts.Data["action_route"], parts.Data["action_args_json"], StayRingPushKinds.RingTag(enquiryId),
            0, NotificationTypes.StayEnquiryRingEnded, nowUtc.AddSeconds(StayRingRules.RingEndTtlSeconds), parts.Data, exceptToken), nowUtc, nowUtc);
    }

    // The ring buttons (use cases 5 and 6): the missed ring is cancelled and the ring stops on the owner's other devices. The enquiry
    // is never read for writing and never changed. Caller saves.
    public static async Task AnswerAsync(ApplicationDbContext db, Guid enquiryId, Guid ownerUserId, string outcome, string? answeringToken, DateTime nowUtc,
        CancellationToken ct = default)
    {
        await CancelMissedAsync(db, [StayRingPushKinds.MissedKey(enquiryId)], ct);
        if (answeringToken is not null)
            AddRingEnd(db, StayRingPushKinds.AnswerEndKey(enquiryId, Guid.NewGuid()), enquiryId, ownerUserId, outcome, nowUtc, answeringToken);
    }

    internal const string CancelMissedSql = """
        UPDATE "OutboxMessages" SET "PublishedAt" = now()
        WHERE "Key" = ANY(@keys) AND "PublishedAt" IS NULL
        RETURNING "Key" AS "Value"
        """;

    // Marks pending missed rows as done (never sent). Returns the keys this call cancelled, so only one caller writes their ring end.
    public static async Task<List<string>> CancelMissedAsync(ApplicationDbContext db, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return [];
        var p = new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys.ToArray() };
        return await db.Database.SqlQueryRaw<string>(CancelMissedSql, p).ToListAsync(ct);
    }

    internal const string LiveRingsOfOwnerSql = """
        SELECT "Key" AS "Value" FROM "OutboxMessages"
        WHERE "PublishedAt" IS NULL AND "NextAttemptAt" > now() AND "NextAttemptAt" <= now() + make_interval(secs => @window)
          AND "Key" LIKE 'stayringmiss:%' AND ("Payload"::jsonb ->> 'userId') = @user::text
        """;

    // Use case 16: suspend or role change in the caller's unit. Every live ring of the owner ends (reason Closed) and its missed ring
    // is cancelled; the caller stamps the "stayringend:" rows with the token copy before it deletes the tokens.
    public static async Task<int> EndLiveRingsOfOwnerAsync(ApplicationDbContext db, Guid ownerUserId, DateTime nowUtc, CancellationToken ct = default)
    {
        var keys = await db.Database.SqlQueryRaw<string>(LiveRingsOfOwnerSql,
            new NpgsqlParameter("window", NpgsqlDbType.Integer) { Value = StayRingRules.RingSeconds + 60 },
            new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = ownerUserId }).ToListAsync(ct);
        var cancelled = await CancelMissedAsync(db, keys, ct);
        foreach (var key in cancelled)
            if (StayRingPushKinds.TryParseMissedKey(key, out var enquiryId))
                AddRingEnd(db, StayRingPushKinds.RingEndKey(enquiryId), enquiryId, ownerUserId, StayRingEndReasons.Closed, nowUtc);
        return cancelled.Count;
    }

    public static bool IsRingEndKey(string key) => key.StartsWith(StayRingPushKinds.RingEndKeyPrefix, StringComparison.Ordinal);

    private static void Add(ApplicationDbContext db, OutboxPushPayload push, DateTime nowUtc, DateTime dueUtc)
        => db.OutboxMessages.Add(new OutboxMessage
        {
            Key = push.Key,
            Exchange = OutboxTargets.PushExchange,
            RoutingKey = OutboxTargets.PushRoutingKey,
            Payload = JsonSerializer.Serialize(push, Json),
            CreatedAt = nowUtc,
            NextAttemptAt = dueUtc,
        });
}

// The readiness test ring: the ring shape with kind stay_ring_test, enquiryId empty and expiresAt 30 seconds ahead (like taxi D12).
public static class StayRingTestRing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OutboxMessage Outbox(Guid userId, DateTime nowUtc)
    {
        var expires = nowUtc.AddSeconds(StayRingRules.TestRingSeconds);
        var data = StayRingPayload.BuildTest(nowUtc, expires, userId);
        var push = new OutboxPushPayload(
            StayRingPushKinds.TestKeyPrefix + Guid.NewGuid().ToString("N"), userId, null, data["title"], data["line1"], data["action_route"],
            data["action_args_json"], StayRingPushKinds.TagPrefix, 0, NotificationTypes.StayEnquiryRing, expires, data);
        return new OutboxMessage
        {
            Key = push.Key,
            Exchange = OutboxTargets.PushExchange,
            RoutingKey = OutboxTargets.PushRoutingKey,
            Payload = JsonSerializer.Serialize(push, Json),
            CreatedAt = nowUtc,
            NextAttemptAt = nowUtc,
        };
    }
}

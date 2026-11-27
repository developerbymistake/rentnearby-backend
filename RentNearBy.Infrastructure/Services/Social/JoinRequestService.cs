using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 2.5: join requests of public trips. Accept writes a trip MEMBER only (plus the optional My Buddies row), never a follow.
public sealed class JoinRequestService(
    ApplicationDbContext db, ISeekerLimits seekerLimits, INoticeWriter notices, IBuddyLiveHints hints, ITripGroupChat groupChat,
    PublicTripReader reader, SocialAfterCommit afterCommit, TimeProvider clock, ILogger<JoinRequestService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // The post as the requester sees it, under the trip row lock: live, visible, his gender, seats, his own state.
    private static readonly string SendStateSql = $"""
        SELECT t."OwnerUserId", t."JoinGender", t."Seats",
               ({PublicTripReader.LivePost("t")}) AS "Live",
               (op."UserId" IS NOT NULL AND {ActiveSeeker("o")}) AS "OwnerOk",
               {BlockedEitherWay("@me", "t.\"OwnerUserId\"")} AS "Blocked",
               (op."IsPrivate" AND NOT {AcceptedFollow("@me", "t.\"OwnerUserId\"")}) AS "PrivateLocked",
               (SELECT p."Gender" FROM "UserProfiles" p WHERE p."UserId" = @me) AS "MyGender",
               COALESCE(me."Name", '') AS "MyName",
               (SELECT count(*) FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}')::int AS "Joined",
               EXISTS (SELECT 1 FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}') AS "Member",
               EXISTS (SELECT 1 FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Removed}') AS "Removed",
               (SELECT count(*) FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."Status" = '{JoinRequestStatuses.Waiting}')::int AS "Pending",
               EXISTS (SELECT 1 FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."RequesterId" = @me AND r."Status" = '{JoinRequestStatuses.Waiting}') AS "Waiting",
               EXISTS (SELECT 1 FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."RequesterId" = @me AND r."Status" = '{JoinRequestStatuses.Declined}'
                       AND r."DecidedAt" > now() - {Interval(SocialLimits.AskAgainAfter)}) AS "Declined",
               {InBuddies("t.\"OwnerUserId\"", "@me")} AS "InOwnersBuddies",
               {AcceptedFollow("@me", "t.\"OwnerUserId\"")} AS "FollowsOwner",
               (SELECT count(*) FROM "TripJoinRequests" r WHERE r."RequesterId" = @me AND r."CreatedAt" >= @month)::int AS "UsedThisMonth"
        FROM "Trips" t
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        LEFT JOIN "UserProfiles" op ON op."UserId" = t."OwnerUserId"
        JOIN "Users" me ON me."Id" = @me
        WHERE t."Id" = @trip AND t."IsPublic"
        """;

    private const string InsertSql = """
        INSERT INTO "TripJoinRequests" ("Id", "TripId", "OwnerUserId", "RequesterId", "ClientRequestId", "Note", "Tier", "Status", "CreatedAt")
        VALUES (gen_random_uuid(), @trip, @owner, @me, @client, @note, @tier, 'Waiting', now())
        ON CONFLICT DO NOTHING
        RETURNING "Id" AS "Value"
        """;

    private const string ReplaySql = """
        SELECT r."Id", r."Status" FROM "TripJoinRequests" r WHERE r."RequesterId" = @me AND r."ClientRequestId" = @client
        """;

    public async Task<BuddyResult<JoinSentDto>> SendAsync(Guid me, Guid tripId, SendJoinRequest request, CancellationToken ct = default)
    {
        var replay = (await db.Database.SqlQueryRaw<ReplayRow>(ReplaySql, Uuid("me", me), Uuid("client", request.ClientRequestId)).ToListAsync(ct)).FirstOrDefault();
        if (replay != null) return BuddyResult<JoinSentDto>.Success(new JoinSentDto { Id = replay.Id, Status = replay.Status });

        string? note = null;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            var (masked, hit) = ContactMask.Apply(request.Note.Trim());
            if (hit && ContactMask.IsOnlyMarks(masked)) return BuddyResult<JoinSentDto>.Fail(SocialErrorTypes.TextNotAllowed, new { field = "note" });
            note = masked.Length > SocialLimits.JoinNoteMax ? masked[..SocialLimits.JoinNoteMax] : masked;
        }
        var limits = await seekerLimits.GetAsync(me, ct);
        Guid owner = default;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockTripAsync(db, tripId, c);
            var s = (await db.Database.SqlQueryRaw<SendState>(SendStateSql, Uuid("me", me), Uuid("trip", tripId),
                Date("today", IstTime.TodayIst(Now)), At("month", SocialMonth.StartUtc(Now))).ToListAsync(c)).FirstOrDefault();
            if (s is null || !s.OwnerOk || s.Blocked || (s.PrivateLocked && s.OwnerUserId != me)) return Fail(SocialErrorTypes.TripPostNotFound);
            if (s.OwnerUserId == me || !s.Live) return Fail(SocialErrorTypes.JoinClosed);
            // Spec 22: everyone sees the post, only the right gender may ask (Women only = women, Men only = men).
            if (!SocialRules.CanJoin(s.JoinGender, s.MyGender)) return Fail(SocialErrorTypes.TripGenderMismatch, new { joinGender = s.JoinGender });
            if (s.Member) return Fail(SocialErrorTypes.AlreadyMember);
            // Owner 2026-11-27: a member the host removed never asks for the same trip again.
            if (s.Removed) return Fail(SocialErrorTypes.CannotRejoin);
            if (s.Waiting) return Fail(SocialErrorTypes.AlreadyRequested);
            if (s.Declined) return Fail(SocialErrorTypes.AskAgainLater);
            if (s.Joined >= Math.Min((int)(s.Seats ?? 0), BuddyLimits.MaxViewers)) return Fail(SocialErrorTypes.TripFull);
            // Owner 2026-11-27: 50 waiting requests per post, counted under the trip row lock (race-safe); a decline frees a slot.
            if (s.Pending >= SocialLimits.PendingRequestsMax) return Fail(SocialErrorTypes.TripRequestsFull);
            // Unlocked slot read (architecture S21): Accept re-checks under the slot lock; taking it here would break the lock order.
            var slots = await TripSlots.ReadAsync(db, me, limits.ActiveTrips, c);
            if (TripSlotRules.IsFull(slots.Count, limits.ActiveTrips)) return Fail(SocialErrorTypes.TripLimitReached, slots);
            if (s.UsedThisMonth >= limits.JoinRequestsPerMonth)
                return Fail(SocialErrorTypes.JoinRequestLimit,
                    SocialProfileService.CapData(limits.JoinRequestsPerMonth, s.UsedThisMonth, limits.CanUpgrade, Now));

            var tier = SocialRules.Tier(s.InOwnersBuddies, s.FollowsOwner);
            var id = (await db.Database.SqlQueryRaw<Guid>(InsertSql, Uuid("trip", tripId), Uuid("owner", s.OwnerUserId), Uuid("me", me),
                Uuid("client", request.ClientRequestId), NullText("note", note),
                new NpgsqlParameter("tier", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = tier }).ToListAsync(c)).FirstOrDefault();
            if (id == Guid.Empty) return Fail(SocialErrorTypes.AlreadyRequested);
            await notices.UpsertAsync([new NoticeUpsert(s.OwnerUserId, NoticeKinds.JoinRequest, id, me, tripId)], c);
            List<PushSpec> pushes = [SocialPushCopy.JoinRequest(s.OwnerUserId, id, tripId, s.MyName)];
            // The request that fills the post also tells the host once per post (same tag: it replaces the request push on the phone).
            if (s.Pending + 1 >= SocialLimits.PendingRequestsMax) pushes.Add(SocialPushCopy.JoinRequestsFull(s.OwnerUserId, tripId));
            await notices.PushAsync(pushes, c);
            await tx.CommitAsync(c);
            owner = s.OwnerUserId;
            return BuddyResult<JoinSentDto>.Success(new JoinSentDto { Id = id, Status = JoinRequestStatuses.Waiting });
        }, ct, logger);
        if (result.Ok)
        {
            notices.Wake();
            await hints.SocialChangedAsync(SocialLiveScopes.JoinRequests, [owner]);
            await hints.SocialChangedAsync(SocialLiveScopes.Limits, [me]);
        }
        return result;

        static BuddyResult<JoinSentDto> Fail(string type, object? data = null) => BuddyResult<JoinSentDto>.Fail(type, data);
    }

    private static readonly string CancelSql = $"""
        UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Cancelled}', "EndedAt" = now()
        WHERE "TripId" = @trip AND "RequesterId" = @me AND "Status" = '{JoinRequestStatuses.Waiting}'
        RETURNING "Id", "OwnerUserId"
        """;

    public async Task CancelMineAsync(Guid me, Guid tripId, CancellationToken ct = default)
    {
        var rows = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var gone = await db.Database.SqlQueryRaw<EndedRow>(CancelSql, Uuid("trip", tripId), Uuid("me", me)).ToListAsync(c);
            foreach (var g in gone) // batch-loop: at most one waiting request per trip and requester
                await notices.DeleteAsync(NoticeKinds.JoinRequest, g.Id, [g.OwnerUserId], c);
            await tx.CommitAsync(c);
            return gone;
        }, ct, logger);
        if (rows.Count > 0) await hints.SocialChangedAsync(SocialLiveScopes.JoinRequests, rows.Select(r => r.OwnerUserId).ToList());
    }

    private static readonly string ListSql = $"""
        SELECT r."Id", r."RequesterId", r."Tier", r."Note", r."CreatedAt", p."Interests", u."CompletedTripsCount" AS "CompletedTrips"
        FROM "TripJoinRequests" r
        JOIN "Users" u ON u."Id" = r."RequesterId" AND {ActiveSeeker("u")}
        LEFT JOIN "UserProfiles" p ON p."UserId" = r."RequesterId"
        WHERE r."TripId" = @trip AND r."Status" = '{JoinRequestStatuses.Waiting}'
          AND EXISTS (SELECT 1 FROM "Trips" t WHERE t."Id" = r."TripId" AND t."OwnerUserId" = @me)
          AND NOT {BlockedEitherWay("@me", "r.\"RequesterId\"")}
        ORDER BY r."Tier", r."CreatedAt" DESC
        LIMIT {SocialLimits.JoinRequestsPageMax}
        """;

    // null = not the caller's public trip (404 TripNotFound).
    public async Task<JoinRequestsPageDto?> ListAsync(Guid me, Guid tripId, CancellationToken ct = default)
    {
        var trip = await reader.ByIdAsync(me, tripId, ct);
        if (trip is null || !trip.IsMine) return null;
        var rows = await db.Database.SqlQueryRaw<ListRow>(ListSql, Uuid("me", me), Uuid("trip", tripId)).ToListAsync(ct);
        var people = (await PersonRowsAsync(db, me, rows.Select(r => r.RequesterId).Distinct().ToList(), ct)).ToDictionary(p => p.UserId);
        return new JoinRequestsPageDto
        {
            Trip = trip,
            Items = rows.Where(r => people.ContainsKey(r.RequesterId)).Select(r => new JoinRequestItemDto
            {
                Id = r.Id, Person = WithLevel(people[r.RequesterId], r.CompletedTrips), Tier = r.Tier, Note = r.Note,
                Interests = (r.Interests ?? []).Take(SocialLimits.OwnerInterestsShown).ToList(), CreatedAt = Utc(r.CreatedAt),
            }).ToList(),
        };
    }

    private const string ReadRequestSql = """
        SELECT r."Id", r."TripId", r."OwnerUserId", r."RequesterId", r."Status" FROM "TripJoinRequests" r WHERE r."Id" = @id AND r."OwnerUserId" = @me
        """;

    private static readonly string AcceptRowSql = $"""
        UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Accepted}', "DecidedAt" = now(), "EndedAt" = now()
        WHERE "Id" = @id AND "OwnerUserId" = @me AND "Status" = '{JoinRequestStatuses.Waiting}'
        RETURNING "Id" AS "Value"
        """;

    private static readonly string AcceptCheckSql = $"""
        SELECT t."JoinGender", t."Seats", t."Title", t."Status" AS "TripStatus",
               (SELECT p."Gender" FROM "UserProfiles" p WHERE p."UserId" = @him) AS "HisGender",
               {BlockedEitherWay("@me", "@him")} AS "Blocked",
               (u."IsActive" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}) AS "HisOk",
               COALESCE(me."Name", '') AS "MyName", COALESCE(u."Name", '') AS "HisName",
               (SELECT count(*) FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}')::int AS "Joined"
        FROM "Trips" t JOIN "Users" u ON u."Id" = @him JOIN "Users" me ON me."Id" = @me
        WHERE t."Id" = @trip
        """;

    private static readonly string UpsertMemberSql = $"""
        INSERT INTO "TripMembers" ("Id", "TripId", "UserId", "Status", "InvitedByUserId", "InvitedAt", "InviteExpiresAt", "JoinedAt", "RespondedAt",
                                   "CreatedAt", "UpdatedAt", "Version", "JoinedVia")
        VALUES (gen_random_uuid(), @trip, @him, '{TripMemberStatuses.Joined}', @me, now(), now(), now(), now(), now(), now(), 1, '{TripJoinedVia.Request}')
        ON CONFLICT ("TripId", "UserId") DO UPDATE SET "Status" = '{TripMemberStatuses.Joined}', "JoinedVia" = '{TripJoinedVia.Request}',
            "InvitedByUserId" = @me, "JoinedAt" = now(), "RespondedAt" = now(), "EndedAt" = NULL, "UpdatedAt" = now(),
            "Version" = "TripMembers"."Version" + 1
        RETURNING "Id" AS "Value"
        """;

    private static readonly string OtherMembersSql = $"""
        SELECT m."UserId" AS "Value" FROM "TripMembers" m WHERE m."TripId" = @trip AND m."Status" = '{TripMemberStatuses.Joined}' AND m."UserId" <> @him
        """;

    public async Task<BuddyResult<JoinAcceptedDto>> AcceptAsync(Guid me, Guid requestId, bool addToBuddies, CancellationToken ct = default)
    {
        var req = (await db.Database.SqlQueryRaw<RequestRow>(ReadRequestSql, Uuid("id", requestId), Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        if (req is null || req.Status != JoinRequestStatuses.Waiting) return BuddyResult<JoinAcceptedDto>.Fail(SocialErrorTypes.JoinRequestGone);
        var him = req.RequesterId;
        var tripId = req.TripId;
        var hisLimit = (await seekerLimits.GetAsync(him, ct)).ActiveTrips;

        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            // Lock order: requester's trip slots -> owner as sender (only for the My Buddies cap) -> pair -> owner's trip row.
            await BuddyLocks.LockTripSlotsAsync(db, him, c);
            if (addToBuddies) await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            if (!await BuddyLocks.LockOwnedTripAsync(db, tripId, me, c)) return Fail(SocialErrorTypes.JoinRequestGone);
            var accepted = (await db.Database.SqlQueryRaw<Guid>(AcceptRowSql, Uuid("id", requestId), Uuid("me", me)).ToListAsync(c)).FirstOrDefault();
            if (accepted == Guid.Empty) return Fail(SocialErrorTypes.JoinRequestGone);

            var s = (await db.Database.SqlQueryRaw<AcceptCheck>(AcceptCheckSql, Uuid("me", me), Uuid("him", him), Uuid("trip", tripId)).ToListAsync(c)).First();
            var seats = Math.Min((int)(s.Seats ?? 0), BuddyLimits.MaxViewers);
            if (s.Joined >= seats) return Fail(SocialErrorTypes.TripFull, new { seatsLeft = 0 });
            if (s.Blocked || !s.HisOk || s.TripStatus == TripStatuses.Completed || !SocialRules.CanJoin(s.JoinGender, s.HisGender))
                return Fail(SocialErrorTypes.CouldNotAdd);
            var slots = await TripSlots.ReadAsync(db, him, hisLimit, c);
            if (TripSlotRules.IsFull(slots.Count, hisLimit)) return Fail(SocialErrorTypes.CouldNotAdd);

            var memberId = (await db.Database.SqlQueryRaw<Guid>(UpsertMemberSql, Uuid("trip", tripId), Uuid("him", him), Uuid("me", me)).ToListAsync(c)).First();
            // At the cap or not a follower: skipped silently, the accept never fails (contract 1.4 / line 223).
            if (addToBuddies) await InsertBuddyCappedAsync(db, me, him, c);
            var roomId = await groupChat.OnMemberJoinedAsync(tripId, him, c);
            var others = await db.Database.SqlQueryRaw<Guid>(OtherMembersSql, Uuid("trip", tripId), Uuid("him", him)).ToListAsync(c);
            await notices.DeleteAsync(NoticeKinds.JoinRequest, requestId, [me], c);
            await notices.UpsertAsync([new NoticeUpsert(him, NoticeKinds.JoinAccepted, tripId, me),
                .. others.Select(o => new NoticeUpsert(o, NoticeKinds.TripJoined, tripId, him))], c);
            await notices.PushAsync([SocialPushCopy.JoinAccepted(him, requestId, tripId, s.MyName)], c);
            await tx.CommitAsync(c);
            afterCommit.Wake();
            afterCommit.Hint(SocialLiveScopes.MyJoinRequests, him);
            afterCommit.Hint(SocialLiveScopes.JoinRequests, me);
            afterCommit.Hint(SocialLiveScopes.Posts, me);
            return BuddyResult<JoinAcceptedDto>.Success(new JoinAcceptedDto { MemberId = memberId, SeatsLeft = Math.Max(0, (s.Seats ?? 0) - s.Joined - 1), RoomId = roomId });
        }, ct, logger);
        if (result.Ok)
        {
            await afterCommit.FlushAsync(ct);
            await hints.TripViewChangedAsync(tripId, [me, him]);
            if (addToBuddies) await hints.BuddiesChangedAsync([me]);
        }
        return result;

        static BuddyResult<JoinAcceptedDto> Fail(string type, object? data = null) => BuddyResult<JoinAcceptedDto>.Fail(type, data);
    }

    private static readonly string DeclineSql = $"""
        UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Declined}', "DecidedAt" = now(), "EndedAt" = now()
        WHERE "Id" = @id AND "OwnerUserId" = @me AND "Status" = '{JoinRequestStatuses.Waiting}'
        RETURNING "Id", "TripId", "RequesterId"
        """;

    public async Task<BuddyResult<bool>> DeclineAsync(Guid me, Guid requestId, CancellationToken ct = default)
    {
        var row = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var d = (await db.Database.SqlQueryRaw<DeclinedRow>(DeclineSql, Uuid("id", requestId), Uuid("me", me)).ToListAsync(c)).FirstOrDefault();
            if (d is null) return null;
            await notices.DeleteAsync(NoticeKinds.JoinRequest, requestId, [me], c);
            await notices.UpsertAsync([new NoticeUpsert(d.RequesterId, NoticeKinds.JoinDeclined, d.TripId, me)], c);
            await tx.CommitAsync(c);
            return d;
        }, ct, logger);
        if (row is null) return BuddyResult<bool>.Fail(SocialErrorTypes.JoinRequestGone);
        await hints.SocialChangedAsync(SocialLiveScopes.MyJoinRequests, [row.RequesterId]);
        return BuddyResult<bool>.Success(true);
    }

    private sealed class ReplayRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private sealed class SendState
    {
        public Guid OwnerUserId { get; set; }
        public string JoinGender { get; set; } = JoinGenders.Anyone;
        public short? Seats { get; set; }
        public bool Live { get; set; }
        public bool OwnerOk { get; set; }
        public bool Blocked { get; set; }
        public bool PrivateLocked { get; set; }
        public string? MyGender { get; set; }
        public string MyName { get; set; } = string.Empty;
        public int Joined { get; set; }
        public bool Member { get; set; }
        public bool Removed { get; set; }
        public int Pending { get; set; }
        public bool Waiting { get; set; }
        public bool Declined { get; set; }
        public bool InOwnersBuddies { get; set; }
        public bool FollowsOwner { get; set; }
        public int UsedThisMonth { get; set; }
    }

    private sealed class EndedRow
    {
        public Guid Id { get; set; }
        public Guid OwnerUserId { get; set; }
    }

    private sealed class ListRow
    {
        public Guid Id { get; set; }
        public Guid RequesterId { get; set; }
        public short Tier { get; set; }
        public string? Note { get; set; }
        public DateTime CreatedAt { get; set; }
        public string[]? Interests { get; set; }
        public int CompletedTrips { get; set; }
    }

    private sealed class RequestRow
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public Guid OwnerUserId { get; set; }
        public Guid RequesterId { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private sealed class AcceptCheck
    {
        public string JoinGender { get; set; } = JoinGenders.Anyone;
        public short? Seats { get; set; }
        public string Title { get; set; } = string.Empty;
        public string TripStatus { get; set; } = string.Empty;
        public string? HisGender { get; set; }
        public bool Blocked { get; set; }
        public bool HisOk { get; set; }
        public string MyName { get; set; } = string.Empty;
        public string HisName { get; set; } = string.Empty;
        public int Joined { get; set; }
    }

    private sealed class DeclinedRow
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public Guid RequesterId { get; set; }
    }
}

// Hourly: Waiting requests of trips that are no longer live posts end Expired (contract 2.5).
public sealed class JoinRequestExpiryJob(ApplicationDbContext db, TimeProvider clock) : IBuddyFeatureJob
{
    public string Name => "social-join-expiry";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public TimeOnly? DailyAtIst => null;

    private static readonly string ExpireSql = $"""
        WITH r AS (
          UPDATE "TripJoinRequests" j SET "Status" = '{JoinRequestStatuses.Expired}', "EndedAt" = now()
          WHERE j."Status" = '{JoinRequestStatuses.Waiting}'
            AND NOT EXISTS (SELECT 1 FROM "Trips" t WHERE t."Id" = j."TripId" AND {PublicTripReader.LivePost("t")})
          RETURNING j."Id"),
        n AS (DELETE FROM "UserNotices" WHERE "Kind" = '{NoticeKinds.JoinRequest}' AND "TripId" IN (SELECT "Id" FROM r))
        SELECT count(*)::int AS "Value" FROM r
        """;

    public async Task<int> RunAsync(CancellationToken ct)
        => (await db.Database.SqlQueryRaw<int>(ExpireSql, Date("today", IstTime.TodayIst(clock.GetUtcNow().UtcDateTime))).ToListAsync(ct)).First();
}

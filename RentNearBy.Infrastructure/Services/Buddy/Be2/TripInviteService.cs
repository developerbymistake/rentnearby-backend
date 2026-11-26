using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// S2, S3, S7, S8, S9 (contract 2.2). Lock order: trip-slots (S8 only) -> pair locks -> trip row -> TripMembers -> UserNotices -> OutboxMessages.
public sealed class TripInviteService(
    ApplicationDbContext db, INoticeWriter notices, IBuddyLiveHints hints, TimeProvider clock, ILogger<TripInviteService> logger,
    ISeekerLimits seekerLimits, ITripGroupChat groupChat, SocialAfterCommit afterCommit)
{
    internal static readonly string CandidatesSql = $"""
        SELECT x.id AS "UserId", (u."Id" IS NOT NULL) AS "Exists", COALESCE(u."IsActive" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}, FALSE) AS "IsActive",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @owner AND b."BuddyUserId" = x.id) AS "IsBuddy",
               EXISTS (SELECT 1 FROM "UserBlocks" k
                       WHERE (k."BlockerId" = @owner AND k."BlockedId" = x.id)
                          OR (k."BlockerId" = x.id AND k."BlockedId" = @owner)) AS "Blocked",
               m."Status" AS "MemberStatus", m."InviteExpiresAt", m."RespondedAt",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = @trip AND j."Status" = 'Joined') AS "JoinedViewers",
               (SELECT count(*)::int FROM "TripMembers" w
                WHERE w."TripId" = @trip AND {LooksWaitingWhere}) AS "WaitingInvites"
        FROM unnest(@ids) AS x(id)
        LEFT JOIN "Users" u ON u."Id" = x.id
        LEFT JOIN "TripMembers" m ON m."TripId" = @trip AND m."UserId" = x.id
        """;

    // A waiting or joined row is never touched (the guard repeats the outcome rule under the lock).
    private const string UpsertSql = """
        INSERT INTO "TripMembers" AS m ("Id", "TripId", "UserId", "Status", "InvitedByUserId", "InvitedAt", "InviteExpiresAt", "CreatedAt", "UpdatedAt", "Version")
        SELECT gen_random_uuid(), @trip, x.id, 'Invited', @owner, now(), @expires, now(), now(), 1
        FROM unnest(@ids) AS x(id)
        ON CONFLICT ("TripId", "UserId") DO UPDATE SET
            "Status" = 'Invited', "InvitedByUserId" = EXCLUDED."InvitedByUserId", "InvitedAt" = now(),
            "InviteExpiresAt" = EXCLUDED."InviteExpiresAt", "RespondedAt" = NULL, "EndedAt" = NULL,
            "UpdatedAt" = now(), "Version" = m."Version" + 1
        WHERE m."Status" <> 'Joined' AND NOT (m."Status" = 'Invited' AND m."InviteExpiresAt" > now())
        RETURNING m."Id", m."UserId", m."Version"
        """;

    private const string CancelSql = """
        UPDATE "TripMembers" SET "Status" = 'Cancelled', "EndedAt" = now(), "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "TripId" = @trip AND "UserId" = @user AND "Status" IN ('Invited', 'Declined')
        """;

    private const string AcceptSql = """
        UPDATE "TripMembers" SET "Status" = 'Joined', "JoinedAt" = now(), "RespondedAt" = now(), "EndedAt" = NULL,
               "ExpensePromptDoneAt" = NULL, "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "Id" = @member AND "Status" = 'Invited' AND "InviteExpiresAt" > now()
        RETURNING "Version" AS "Value"
        """;

    private const string DeclineSql = """
        UPDATE "TripMembers" SET "Status" = 'Declined', "RespondedAt" = now(), "EndedAt" = now(), "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "Id" = @member AND "Status" = 'Invited' AND "InviteExpiresAt" > now()
        """;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // S2. userIds are distinct (validator). Outcomes in the order of the request.
    public async Task<SharingResult<TripInviteResultDto>> InviteAsync(Guid tripId, Guid ownerId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairsAsync(db, ownerId, ids, c);
            var trip = await LockTripAsync(db, tripId, c);
            if (trip is null || trip.OwnerUserId != ownerId) return SharingResult<TripInviteResultDto>.Fail(BuddyErrorTypes.TripNotFound);
            var now = Now;
            if (TripSharingRules.IsEnded(trip.Status, trip.EndDate, TripSharingRules.TodayIst(now)))
                return SharingResult<TripInviteResultDto>.Fail(BuddyErrorTypes.TripCompleted);
            if (trip.SpotCount <= 0) return SharingResult<TripInviteResultDto>.Fail(BuddyErrorTypes.NoPlaces);

            var rows = await db.Database.SqlQueryRaw<CandidateRow>(CandidatesSql,
                Id("owner", ownerId), Id("trip", tripId), Ids("ids", ids)).ToListAsync(c);
            var joined = rows.Count > 0 ? rows[0].JoinedViewers : 0;
            var waiting = rows.Count > 0 ? rows[0].WaitingInvites : 0;
            var outcomes = rows.ToDictionary(r => r.UserId, r => TripSharingRules.Classify(r.ToCandidate(), ownerId, now));
            var toInvite = ids.Where(id => outcomes.GetValueOrDefault(id, InviteOutcomes.NotBuddy) == InviteOutcomes.Invited).ToArray();
            if (toInvite.Length > TripSharingRules.SeatsLeft(joined, waiting))
                return SharingResult<TripInviteResultDto>.Fail(BuddyErrorTypes.TripFull, new { seatsLeft = TripSharingRules.SeatsLeft(joined, waiting) });

            var written = new Dictionary<Guid, (Guid MemberId, int Version)>();
            if (toInvite.Length > 0)
            {
                var expires = TripSharingRules.InviteExpiresAt(now, trip.EndDate);
                var upserted = await db.Database.SqlQueryRaw<UpsertRow>(UpsertSql,
                    Id("trip", tripId), Id("owner", ownerId), Time("expires", expires), Ids("ids", toInvite)).ToListAsync(c);
                foreach (var u in upserted) written[u.UserId] = (u.Id, u.Version);
                var invited = written.Keys.ToList();
                await notices.UpsertAsync(written.Select(w => new NoticeUpsert(w.Key, NoticeKinds.TripInvite, tripId, ownerId, w.Value.MemberId)).ToList(), c);
                await notices.DeleteAsync(NoticeKinds.TripRemoved, tripId, invited, c);
                await notices.PushAsync(written.Select(w =>
                    BuddyPushCopy.TripInvite(w.Key, w.Value.MemberId, w.Value.Version, tripId, trip.OwnerName, trip.Title)).ToList(), c);
            }
            await tx.CommitAsync(c);

            var dto = new TripInviteResultDto
            {
                Results = ids.Select(id =>
                {
                    var outcome = outcomes.GetValueOrDefault(id, InviteOutcomes.NotBuddy);
                    if (outcome == InviteOutcomes.Invited && !written.ContainsKey(id)) outcome = InviteOutcomes.AlreadyInvited;
                    return new TripInviteOutcomeDto
                    {
                        UserId = id,
                        Outcome = outcome,
                        MemberId = written.TryGetValue(id, out var w) ? w.MemberId : null,
                    };
                }).ToList(),
                SeatsLeft = TripSharingRules.SeatsLeft(joined, waiting + written.Count),
            };
            return SharingResult<TripInviteResultDto>.Success(dto);
        }, ct, logger);
        if (result.Ok && result.Value!.Results.Any(r => r.Outcome == InviteOutcomes.Invited)) notices.Wake();
        return result;
    }

    // S3: idempotent; 200 also when no invite waits.
    public async Task<SharingResult<TripStatusDto>> CancelAsync(Guid tripId, Guid ownerId, Guid userId, CancellationToken ct = default)
        => await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var trip = await LockTripAsync(db, tripId, c);
            if (trip is null || trip.OwnerUserId != ownerId) return SharingResult<TripStatusDto>.Fail(BuddyErrorTypes.TripNotFound);
            var changed = await db.Database.ExecuteSqlRawAsync(CancelSql, [Id("trip", tripId), Id("user", userId)], c);
            if (changed > 0) await notices.DeleteAsync(NoticeKinds.TripInvite, tripId, [userId], c);
            await tx.CommitAsync(c);
            return SharingResult<TripStatusDto>.Success(new TripStatusDto { Status = TripMemberStatuses.Cancelled });
        }, ct, logger);

    // S7.
    public async Task<SharingResult<TripInvitePreviewDto>> PreviewAsync(Guid memberId, Guid callerId, CancellationToken ct = default)
    {
        var now = Now;
        var row = await ReadInviteAsync(db, memberId, ct);
        if (row is null || row.UserId != callerId || row.Blocked) return Ended(null);
        var alreadyMember = row.Status == TripMemberStatuses.Joined;
        if (!alreadyMember && !TripSharingRules.IsWaitingInvite(row.Status, row.InviteExpiresAt, now)) return Ended(row);
        var cities = LegCities(row.PlanJson, row.CityName);
        return SharingResult<TripInvitePreviewDto>.Success(new TripInvitePreviewDto
        {
            MemberId = row.MemberId,
            TripId = alreadyMember ? row.TripId : null,
            Owner = new TripOwnerRefDto
            {
                Name = row.OwnerName, Username = row.OwnerUsername,
                PhotoUrl = CloudinaryThumb.Apply(row.OwnerPhotoUrl, CloudinaryThumb.PersonFace),
            },
            Title = row.Title,
            LegCities = cities,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            DayCount = row.DayCount,
            CityCount = cities.Count,
            TravellerCount = 1 + row.JoinedViewers,
            ExpiresAt = row.InviteExpiresAt,
            AlreadyMember = alreadyMember,
        });

        SharingResult<TripInvitePreviewDto> Ended(InviteRow? r) => SharingResult<TripInvitePreviewDto>.Fail(BuddyErrorTypes.InviteEnded, EndedData(r, now));
    }

    // S8: 200 also on a repeat. The caller's slot lock then the trip lock; the conditional update serialises against cancel, block and expiry.
    public async Task<SharingResult<TripJoinedDto>> AcceptAsync(Guid memberId, Guid callerId, CancellationToken ct = default)
    {
        var first = await ReadInviteLeanAsync(db, memberId, ct);
        if (first is null || first.UserId != callerId) return EndedResult<TripJoinedDto>(null);
        if (first.Status == TripMemberStatuses.Joined) return Joined(first.TripId);

        Guid? ownerToTell = null;
        var activeTripLimit = (await seekerLimits.GetAsync(callerId, ct)).ActiveTrips;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockTripSlotsAsync(db, callerId, c);
            var trip = await LockTripAsync(db, first.TripId, c);
            if (trip is null) return EndedResult<TripJoinedDto>(null);
            // PLAN_STATES_SOCIAL 1: a trip kept inactive after its owner's Plus ended is read-only; joining it is a write (409, the
            // invite keeps waiting and works again once the owner's trip is active).
            if (trip.InactiveByPlan)
                return SharingResult<TripJoinedDto>.Fail(SocialErrorTypes.TripInactiveByPlan,
                    new { limit = (await seekerLimits.GetAsync(trip.OwnerUserId, c)).ActiveTrips }, SocialErrorTypes.StatusOf(SocialErrorTypes.TripInactiveByPlan));
            var row = await ReadInviteLeanAsync(db, memberId, c);
            if (row is null || row.UserId != callerId) return EndedResult<TripJoinedDto>(null);
            if (row.Status == TripMemberStatuses.Joined) return Joined(row.TripId);
            if (row.Blocked) return EndedResult<TripJoinedDto>(null);
            if (!TripSharingRules.IsWaitingInvite(row.Status, row.InviteExpiresAt, Now)) return EndedResult<TripJoinedDto>(row);
            if (row.JoinedViewers >= BuddyLimits.MaxViewers)
                return SharingResult<TripJoinedDto>.Fail(BuddyErrorTypes.TripFull, new { seatsLeft = 0 });
            // At the trip limit nothing is written: the invite keeps waiting until a slot is freed.
            if (await TripSlots.ReadAsync(db, callerId, activeTripLimit, c) is { } slots && TripSlotRules.IsFull(slots.Count, activeTripLimit))
                return SharingResult<TripJoinedDto>.Fail(BuddyErrorTypes.TripLimitReached, slots);

            var version = (await db.Database.SqlQueryRaw<int>(AcceptSql, Id("member", memberId)).ToListAsync(c)).FirstOrDefault();
            if (version == 0)
            {
                var again = await ReadInviteLeanAsync(db, memberId, c);
                return again?.Status == TripMemberStatuses.Joined ? Joined(again.TripId) : EndedResult<TripJoinedDto>(again);
            }
            await notices.DeleteAsync(NoticeKinds.TripInvite, row.TripId, [callerId], c);
            await notices.UpsertAsync([new NoticeUpsert(row.OwnerUserId, NoticeKinds.TripJoined, row.TripId, callerId)], c);
            await notices.PushAsync([BuddyPushCopy.TripMemberJoined(row.OwnerUserId, memberId, version, row.TripId,
                row.InviteeName, row.Title, row.JoinedViewers + 2)], c);
            await groupChat.OnMemberJoinedAsync(row.TripId, callerId, c);
            await tx.CommitAsync(c);
            ownerToTell = row.OwnerUserId;
            return Joined(row.TripId);
        }, ct, logger);

        if (ownerToTell is { } owner)
        {
            await afterCommit.FlushAsync();
            notices.Wake();
            await hints.TripViewChangedAsync(first.TripId, [owner, callerId]);
        }
        return result;
    }

    // S9: idempotent and silent.
    public async Task<SharingResult<TripStatusDto>> DeclineAsync(Guid memberId, Guid callerId, CancellationToken ct = default)
    {
        var first = await ReadInviteLeanAsync(db, memberId, ct);
        if (first is null || first.UserId != callerId) return EndedResult<TripStatusDto>(null);
        if (first.Status == TripMemberStatuses.Declined) return Declined();

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await LockTripAsync(db, first.TripId, c);
            var changed = await db.Database.ExecuteSqlRawAsync(DeclineSql, [Id("member", memberId)], c);
            if (changed == 0)
            {
                var row = await ReadInviteLeanAsync(db, memberId, c);
                await tx.CommitAsync(c);
                return row?.Status == TripMemberStatuses.Declined ? Declined() : EndedResult<TripStatusDto>(row);
            }
            await notices.DeleteAsync(NoticeKinds.TripInvite, first.TripId, [callerId], c);
            await tx.CommitAsync(c);
            return Declined();
        }, ct, logger);
    }

    private static SharingResult<TripJoinedDto> Joined(Guid tripId)
        => SharingResult<TripJoinedDto>.Success(new TripJoinedDto { TripId = tripId, Status = TripMemberStatuses.Joined });

    private static SharingResult<TripStatusDto> Declined()
        => SharingResult<TripStatusDto>.Success(new TripStatusDto { Status = TripMemberStatuses.Declined });

    private SharingResult<T> EndedResult<T>(InviteRow? row) => SharingResult<T>.Fail(BuddyErrorTypes.InviteEnded, EndedData(row, Now));

    // Title and owner name only for the caller's own ended invite; a block, a foreign id or a gone trip read Unknown.
    internal static InviteEndedDataDto EndedData(InviteRow? row, DateTime nowUtc)
    {
        if (row is null || row.Blocked) return new InviteEndedDataDto { Reason = InviteEndedReasons.Unknown };
        var reason = TripSharingRules.EndedReason(row.Status, row.InviteExpiresAt, nowUtc);
        return reason == InviteEndedReasons.Unknown
            ? new InviteEndedDataDto { Reason = reason }
            : new InviteEndedDataDto { Reason = reason, Title = row.Title, OwnerName = row.OwnerName };
    }

    private sealed class CandidateRow
    {
        public Guid UserId { get; set; }
        public bool Exists { get; set; }
        public bool IsActive { get; set; }
        public bool IsBuddy { get; set; }
        public bool Blocked { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public int JoinedViewers { get; set; }
        public int WaitingInvites { get; set; }

        public InviteCandidate ToCandidate() => new(UserId, Exists, IsActive, IsBuddy, Blocked, MemberStatus, InviteExpiresAt, RespondedAt);
    }

    private sealed class UpsertRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public int Version { get; set; }
    }
}

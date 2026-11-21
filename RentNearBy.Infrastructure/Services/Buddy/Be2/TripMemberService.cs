using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// S1, S12, S13, S16, S17 (contract 2.2).
public sealed class TripMemberService(
    ApplicationDbContext db, ITripAccess access, ITripMoneyCards money, INoticeWriter notices, IBuddyLiveHints hints,
    TimeProvider clock, ILogger<TripMemberService> logger)
{
    // Owner row, Joined rows and (owner caller only) waiting invites, with "is the caller's buddy" per person.
    private static readonly string PeopleSql = $"""
        SELECT x."UserId", x."Name", x."Username", x."Kind", x."MemberId", x."JoinedAt", x."InvitedAt", x."InviteExpiresAt",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = x."UserId") AS "IsBuddy"
        FROM (
          SELECT t."OwnerUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", 'Owner'::text AS "Kind",
                 NULL::uuid AS "MemberId", NULL::timestamptz AS "JoinedAt", NULL::timestamptz AS "InvitedAt", NULL::timestamptz AS "InviteExpiresAt"
          FROM "Trips" t JOIN "Users" u ON u."Id" = t."OwnerUserId"
          WHERE t."Id" = @trip
          UNION ALL
          SELECT m."UserId", COALESCE(u."Name", ''), u."Username",
                 (CASE WHEN m."Status" = 'Declined' THEN 'Invited' ELSE m."Status" END)::text, m."Id", m."JoinedAt", m."InvitedAt", m."InviteExpiresAt"
          FROM "TripMembers" m JOIN "Users" u ON u."Id" = m."UserId"
          WHERE m."TripId" = @trip
            AND (m."Status" = 'Joined' OR (@isOwner AND {LooksWaiting("m")}))
        ) x
        ORDER BY (x."Kind" = 'Owner') DESC, x."JoinedAt" NULLS FIRST, x."InvitedAt", x."UserId"
        """;

    private const string RemoveSql = """
        UPDATE "TripMembers" SET "Status" = 'Removed', "EndedAt" = now(), "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "TripId" = @trip AND "UserId" = @user AND "Status" = 'Joined'
        RETURNING "Id", "Version"
        """;

    private const string LeaveSql = """
        UPDATE "TripMembers" SET "Status" = 'Left', "EndedAt" = now(), "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "TripId" = @trip AND "UserId" = @user AND "Status" = 'Joined'
        """;

    private const string MemberStatusSql = """
        SELECT "Status" AS "Value" FROM "TripMembers" WHERE "TripId" = @trip AND "UserId" = @user
        """;

    private const string ShareCountSql = """
        SELECT count(*)::int AS "Value" FROM "TripExpenseShares" WHERE "TripId" = @trip AND "UserId" = @user
        """;

    private const string IsBuddySql = """
        SELECT EXISTS (SELECT 1 FROM "Buddies" b JOIN "Users" u ON u."Id" = b."BuddyUserId"
                       WHERE b."UserId" = @me AND b."BuddyUserId" = @user AND u."IsActive") AS "Value"
        """;

    private static readonly string OptionsSql = $"""
        SELECT t."Id" AS "TripId", t."Title", t."Status", t."StartDate", t."EndDate", t."DayCount", t."SpotCount",
               m."Status" AS "MemberStatus", m."InviteExpiresAt", m."RespondedAt",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = t."Id" AND j."Status" = 'Joined') AS "JoinedViewers",
               (SELECT count(*)::int FROM "TripMembers" w
                WHERE w."TripId" = t."Id" AND {LooksWaitingWhere}) AS "WaitingInvites"
        FROM "Trips" t
        LEFT JOIN "TripMembers" m ON m."TripId" = t."Id" AND m."UserId" = @user
        WHERE t."OwnerUserId" = @me AND t."Status" <> 'Completed' AND (t."EndDate" IS NULL OR t."EndDate" >= @today)
        ORDER BY t."UpdatedAt" DESC, t."Id" DESC
        LIMIT {BuddyLimits.InviteOptionsMax}
        """;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // S1 (2 statements).
    public async Task<SharingResult<TripMembersDto>> GetMembersAsync(Guid tripId, Guid callerId, CancellationToken ct = default)
    {
        var info = await access.GetAsync(tripId, callerId, ct);
        if (info is null || info.Level == TripAccessLevel.Former) return SharingResult<TripMembersDto>.Fail(BuddyErrorTypes.TripNotFound);
        var isOwner = info.Level == TripAccessLevel.Owner;
        var rows = await db.Database.SqlQueryRaw<PersonRow>(PeopleSql,
            Id("me", callerId), Id("trip", tripId), Bool("isOwner", isOwner)).ToListAsync(ct);
        return SharingResult<TripMembersDto>.Success(BuildMembers(info, rows, callerId, Now));
    }

    internal static TripMembersDto BuildMembers(TripAccessInfo info, IReadOnlyList<PersonRow> rows, Guid callerId, DateTime nowUtc)
    {
        var isOwner = info.Level == TripAccessLevel.Owner;
        var joined = rows.Where(r => r.Kind is TripAccessStatuses.Owner or TripMemberStatuses.Joined).ToList();
        var invited = isOwner ? rows.Where(r => r.Kind == TripMemberStatuses.Invited).ToList() : [];
        var viewers = joined.Count(r => r.Kind == TripMemberStatuses.Joined);
        var seats = TripSharingRules.SeatsLeft(viewers, invited.Count);
        var ended = TripSharingRules.IsEnded(info.Status, info.EndDate, TripSharingRules.TodayIst(nowUtc));
        return new TripMembersDto
        {
            TripId = info.TripId,
            Title = info.Title,
            Role = isOwner ? TripSharingRoles.Owner : TripSharingRoles.Viewer,
            OwnerName = info.OwnerName,
            TravellerCount = joined.Count,
            MaxTravellers = BuddyLimits.MaxTravellers,
            PendingInvites = isOwner ? invited.Count : 0,
            SeatsLeft = isOwner ? seats : 0,
            Joined = joined.Select(r => new TripJoinedPersonDto
            {
                UserId = r.UserId,
                Name = r.Name,
                Username = TripSharingRules.VisibleUsername(isOwner, callerId, r.UserId, r.Kind == TripAccessStatuses.Owner, r.IsBuddy, r.Username),
                Initial = MoneyPeople.Initial(r.Name),
                Last4 = MoneyPeople.Last4(r.Username),
                Role = r.Kind == TripAccessStatuses.Owner ? TripSharingRoles.Owner : TripSharingRoles.Viewer,
                IsMe = r.UserId == callerId,
                JoinedAt = r.Kind == TripAccessStatuses.Owner ? null : r.JoinedAt,
            }).ToList(),
            Invited = invited.Select(r => new TripInvitedPersonDto
            {
                MemberId = r.MemberId ?? Guid.Empty,
                UserId = r.UserId,
                Name = r.Name,
                Username = r.Username,
                Initial = MoneyPeople.Initial(r.Name),
                InvitedAt = r.InvitedAt ?? default,
                ExpiresAt = r.InviteExpiresAt ?? default,
            }).ToList(),
            CanInvite = isOwner && seats > 0 && !ended,
            CanLeave = !isOwner,
        };
    }

    // S12.
    public async Task<SharingResult<TripRemovedDto>> RemoveAsync(Guid tripId, Guid ownerId, Guid userId, CancellationToken ct = default)
    {
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var trip = await LockTripAsync(db, tripId, c);
            if (trip is null || trip.OwnerUserId != ownerId) return SharingResult<TripRemovedDto>.Fail(BuddyErrorTypes.TripNotFound);
            var removed = (await db.Database.SqlQueryRaw<VersionRow>(RemoveSql, Id("trip", tripId), Id("user", userId)).ToListAsync(c)).FirstOrDefault();
            if (removed is null) return SharingResult<TripRemovedDto>.Fail(BuddyErrorTypes.NotOnTrip);
            var shares = (await db.Database.SqlQueryRaw<int>(ShareCountSql, Id("trip", tripId), Id("user", userId)).ToListAsync(c)).FirstOrDefault();
            await notices.UpsertAsync([new NoticeUpsert(userId, NoticeKinds.TripRemoved, tripId, ownerId)], c);
            await notices.DeletePairsAsync(tripId, NoticeKinds.GoneOnRemove.Select(k => (k, userId)).ToList(), c);
            await notices.PushAsync([BuddyPushCopy.TripMemberRemoved(userId, removed.Id, removed.Version, tripId, trip.OwnerName, trip.Title)], c);
            await tx.CommitAsync(c);
            return SharingResult<TripRemovedDto>.Success(new TripRemovedDto { Status = TripMemberStatuses.Removed, SharesExpenses = shares });
        }, ct, logger);
        if (result.Ok)
        {
            notices.Wake();
            await hints.TripViewChangedAsync(tripId, [ownerId, userId]);
        }
        return result;
    }

    // S13: idempotent for a Former caller; no notification.
    public async Task<SharingResult<TripLeftDto>> LeaveAsync(Guid tripId, Guid callerId, CancellationToken ct = default)
    {
        Guid ownerId = default;
        var left = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var trip = await LockTripAsync(db, tripId, c);
            if (trip is null || trip.OwnerUserId == callerId) return false;
            ownerId = trip.OwnerUserId;
            var changed = await db.Database.ExecuteSqlRawAsync(LeaveSql, [Id("trip", tripId), Id("user", callerId)], c);
            if (changed > 0) await notices.DeleteAsync(NoticeKinds.PlanUpdated, tripId, [callerId], c);
            if (changed == 0)
            {
                var status = (await db.Database.SqlQueryRaw<string>(MemberStatusSql, Id("trip", tripId), Id("user", callerId)).ToListAsync(c)).FirstOrDefault();
                if (status is not (TripMemberStatuses.Left or TripMemberStatuses.Removed)) return false;
            }
            await tx.CommitAsync(c);
            return true;
        }, ct, logger);
        if (!left) return SharingResult<TripLeftDto>.Fail(BuddyErrorTypes.TripNotFound);

        await hints.TripViewChangedAsync(tripId, [ownerId, callerId]);
        var standing = (await money.GetStandingsAsync([tripId], callerId, ct)).GetValueOrDefault(tripId);
        return SharingResult<TripLeftDto>.Success(new TripLeftDto { Status = TripMemberStatuses.Left, HasShare = standing?.HasShare ?? false });
    }

    // S16 (2 statements).
    public async Task<SharingResult<TripCountDto>> ExpenseCountAsync(Guid tripId, Guid ownerId, Guid userId, CancellationToken ct = default)
    {
        var info = await access.GetAsync(tripId, ownerId, ct);
        if (info is null || info.Level != TripAccessLevel.Owner) return SharingResult<TripCountDto>.Fail(BuddyErrorTypes.TripNotFound);
        var count = (await db.Database.SqlQueryRaw<int>(ShareCountSql, Id("trip", tripId), Id("user", userId)).ToListAsync(ct)).FirstOrDefault();
        return SharingResult<TripCountDto>.Success(new TripCountDto { Count = count });
    }

    // S17 (2 statements).
    public async Task<SharingResult<List<TripInviteOptionDto>>> InviteOptionsAsync(Guid callerId, Guid userId, CancellationToken ct = default)
    {
        var isBuddy = callerId != userId
            && (await db.Database.SqlQueryRaw<bool>(IsBuddySql, Id("me", callerId), Id("user", userId)).ToListAsync(ct)).FirstOrDefault();
        if (!isBuddy) return SharingResult<List<TripInviteOptionDto>>.Fail(BuddyErrorTypes.PersonNotFound);
        var now = Now;
        var rows = await db.Database.SqlQueryRaw<OptionRow>(OptionsSql,
            Id("me", callerId), Id("user", userId), Date("today", TripSharingRules.TodayIst(now))).ToListAsync(ct);
        return SharingResult<List<TripInviteOptionDto>>.Success(rows.Select(r => new TripInviteOptionDto
        {
            TripId = r.TripId,
            Title = r.Title,
            Status = r.Status,
            StartDate = r.StartDate,
            EndDate = r.EndDate,
            DayCount = r.DayCount,
            TravellerCount = 1 + r.JoinedViewers,
            Option = TripSharingRules.Option(r.MemberStatus, r.InviteExpiresAt, r.RespondedAt, r.SpotCount, r.JoinedViewers, r.WaitingInvites, now),
        }).ToList());
    }

    internal sealed class PersonRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string Kind { get; set; } = string.Empty;
        public Guid? MemberId { get; set; }
        public DateTime? JoinedAt { get; set; }
        public DateTime? InvitedAt { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public bool IsBuddy { get; set; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
    }

    private sealed class OptionRow
    {
        public Guid TripId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public int DayCount { get; set; }
        public int SpotCount { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? InviteExpiresAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public int JoinedViewers { get; set; }
        public int WaitingInvites { get; set; }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// The three seam calls of TripHandlers (contract 2.2 "Changed existing routes"). Never writes Trips.
public sealed class TripSharingHooks(
    ApplicationDbContext db, ITripMoneyCards money, INoticeWriter notices, IBuddyLiveHints hints, TimeProvider clock,
    ILogger<TripSharingHooks> logger) : ITripSharingHooks
{
    private const string SharingSql = """
        SELECT x."UserId", x."Name", x."IsOwner", t."StartDate", t."DayCount",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = @trip AND j."Status" = 'Joined') AS "ViewerCount",
               (SELECT count(*)::int FROM "TripMembers" w
                WHERE w."TripId" = @trip AND w."Status" = 'Invited' AND w."InviteExpiresAt" > now()) AS "PendingInvites"
        FROM (
          SELECT t0."OwnerUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", TRUE AS "IsOwner", NULL::timestamptz AS "JoinedAt"
          FROM "Trips" t0 JOIN "Users" u ON u."Id" = t0."OwnerUserId"
          WHERE t0."Id" = @trip AND t0."OwnerUserId" = @owner
          UNION ALL
          (SELECT m."UserId", COALESCE(u."Name", ''), FALSE, m."JoinedAt"
           FROM "TripMembers" m JOIN "Users" u ON u."Id" = m."UserId"
           WHERE m."TripId" = @trip AND m."Status" = 'Joined'
           ORDER BY m."JoinedAt", m."UserId"
           LIMIT 2)
        ) x
        JOIN "Trips" t ON t."Id" = @trip AND t."OwnerUserId" = @owner
        ORDER BY x."IsOwner" DESC, x."JoinedAt" NULLS FIRST, x."UserId"
        """;

    private const string ViewersSql = """
        SELECT m."UserId", t."Title", COALESCE(o."Name", '') AS "OwnerName"
        FROM "TripMembers" m
        JOIN "Trips" t ON t."Id" = m."TripId"
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        WHERE m."TripId" = @trip AND t."OwnerUserId" = @owner AND m."Status" = 'Joined'
        """;

    private static readonly string[] KeepOnDelete = [NoticeKinds.TripDeleted];

    // 1 statement + money cards (2).
    public async Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SharingRow>(SharingSql, Id("trip", tripId), Id("owner", ownerId)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var head = rows[0];
        var card = (await money.GetCardsAsync([tripId], ownerId, ct)).GetValueOrDefault(tripId);
        return new TripSharingBlockDto
        {
            TravellerCount = 1 + head.ViewerCount,
            ViewerCount = head.ViewerCount,
            PendingInvites = head.PendingInvites,
            FirstTravellers = rows.Take(3).Select(r => new TravellerChipDto { UserId = r.UserId, Name = r.Name, Initial = MoneyPeople.Initial(r.Name) }).ToList(),
            Expenses = TripViewerProjection.Card(card, head.StartDate, head.DayCount, TripSharingRules.TodayIst(clock.GetUtcNow().UtcDateTime)),
        };
    }

    // After the plan save commit: one coalesced, delayed push-only row per viewer (key bucket of 30 minutes). Never throws.
    public async Task OnPlanSavedAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
    {
        try
        {
            var viewers = await ReadViewersAsync(tripId, ownerId, ct);
            if (viewers.Count == 0) return;
            var now = clock.GetUtcNow().UtcDateTime;
            await notices.PushAsync(viewers.Select(v => BuddyPushCopy.TripPlanUpdated(v.UserId, tripId, v.OwnerName, v.Title, now)).ToList(), ct);
            notices.Wake();
            await hints.TripViewChangedAsync(tripId, viewers.Select(v => v.UserId).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "buddy.plan_saved_hook failed for trip {TripId}", tripId);
        }
    }

    // Inside the delete transaction, after the trip lock: TripDeleted notices + pushes to joined viewers, other notices gone.
    public async Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
    {
        var viewers = await ReadViewersAsync(tripId, ownerId, ct);
        await notices.DeleteForTripAsync(tripId, KeepOnDelete, ct);
        if (viewers.Count == 0) return 0;
        await notices.UpsertAsync(viewers.Select(v => new NoticeUpsert(v.UserId, NoticeKinds.TripDeleted, tripId, ownerId)).ToList(), ct);
        await notices.PushAsync(viewers.Select(v => BuddyPushCopy.TripDeletedForViewer(v.UserId, tripId, v.OwnerName, v.Title)).ToList(), ct);
        notices.Wake();
        return viewers.Count;
    }

    private Task<List<ViewerRow>> ReadViewersAsync(Guid tripId, Guid ownerId, CancellationToken ct)
        => db.Database.SqlQueryRaw<ViewerRow>(ViewersSql, Id("trip", tripId), Id("owner", ownerId)).ToListAsync(ct);

    private sealed class SharingRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOwner { get; set; }
        public DateOnly? StartDate { get; set; }
        public int DayCount { get; set; }
        public int ViewerCount { get; set; }
        public int PendingInvites { get; set; }
    }

    private sealed class ViewerRow
    {
        public Guid UserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// The three seam calls of TripHandlers (contract 2.2 "Changed existing routes"). Never writes Trips.
public sealed class TripSharingHooks(
    ApplicationDbContext db, ITripMoneyCards money, INoticeWriter notices, IBuddyLiveHints hints, TimeProvider clock,
    ILogger<TripSharingHooks> logger) : ITripSharingHooks
{
    private static readonly string SharingSql = $"""
        SELECT x."UserId", x."Name", x."IsOwner", x."PhotoUrl", t."StartDate", t."DayCount",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = @trip AND j."Status" = 'Joined') AS "ViewerCount",
               (SELECT count(*)::int FROM "TripMembers" w
                WHERE w."TripId" = @trip AND {LooksWaitingWhere}) AS "PendingInvites"
        FROM (
          SELECT t0."OwnerUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", TRUE AS "IsOwner", NULL::timestamptz AS "JoinedAt",
                 u."ProfilePhotoUrl" AS "PhotoUrl"
          FROM "Trips" t0 JOIN "Users" u ON u."Id" = t0."OwnerUserId"
          WHERE t0."Id" = @trip AND t0."OwnerUserId" = @owner
          UNION ALL
          (SELECT m."UserId", COALESCE(u."Name", ''), FALSE, m."JoinedAt", u."ProfilePhotoUrl"
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
    public Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
        => BuildAsync(tripId, ownerId, readMoney: true, ct);

    // 1 statement: a brand-new trip has no members and no expenses (FB-01).
    public Task<TripSharingBlockDto?> GetNewTripSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
        => BuildAsync(tripId, ownerId, readMoney: false, ct);

    private async Task<TripSharingBlockDto?> BuildAsync(Guid tripId, Guid ownerId, bool readMoney, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SharingRow>(SharingSql, Id("trip", tripId), Id("owner", ownerId)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var head = rows[0];
        var card = readMoney ? (await money.GetCardsAsync([tripId], ownerId, ct)).GetValueOrDefault(tripId) : null;
        return new TripSharingBlockDto
        {
            TravellerCount = 1 + head.ViewerCount,
            ViewerCount = head.ViewerCount,
            PendingInvites = head.PendingInvites,
            FirstTravellers = rows.Take(3).Select(r => new TravellerChipDto
            {
                UserId = r.UserId, Name = r.Name, Initial = MoneyPeople.Initial(r.Name),
                PhotoUrl = CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace),
            }).ToList(),
            Expenses = TripViewerProjection.Card(card, head.StartDate, head.DayCount, TripSharingRules.TodayIst(clock.GetUtcNow().UtcDateTime)),
        };
    }

    // After the plan save commit: one PlanUpdated row per viewer upserted in place, plus the coalesced, delayed push. Never throws.
    public async Task OnPlanSavedAsync(Guid tripId, Guid ownerId, int? changedDay = null, CancellationToken ct = default)
    {
        try
        {
            var viewers = await ReadViewersAsync(tripId, ownerId, ct);
            if (viewers.Count == 0) return;
            var now = clock.GetUtcNow().UtcDateTime;
            await notices.UpsertAsync(viewers.Select(v => new NoticeUpsert(v.UserId, NoticeKinds.PlanUpdated, tripId, ownerId, null, changedDay)).ToList(), ct);
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
    // The handler wakes the relay after commit when viewers were told (contract 5.1).
    public async Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
    {
        var viewers = await ReadViewersAsync(tripId, ownerId, ct);
        await notices.DeleteForTripAsync(tripId, KeepOnDelete, ct);
        if (viewers.Count == 0) return 0;
        await notices.UpsertAsync(viewers.Select(v => new NoticeUpsert(v.UserId, NoticeKinds.TripDeleted, tripId, ownerId)).ToList(), ct);
        await notices.PushAsync(viewers.Select(v => BuddyPushCopy.TripDeletedForViewer(v.UserId, tripId, v.OwnerName, v.Title)).ToList(), ct);
        return viewers.Count;
    }

    private Task<List<ViewerRow>> ReadViewersAsync(Guid tripId, Guid ownerId, CancellationToken ct)
        => db.Database.SqlQueryRaw<ViewerRow>(ViewersSql, Id("trip", tripId), Id("owner", ownerId)).ToListAsync(ct);

    private sealed class SharingRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOwner { get; set; }
        public string? PhotoUrl { get; set; }
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

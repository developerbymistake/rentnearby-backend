using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

public sealed record TripViewResult(TripViewerDto? View, string ETag, bool NotModified);

// S14 (contract 2.2): access; stamp (first travellers + counts); trip; enquiries; taxi summary; money cards (2) = 7.
// A matching If-None-Match answers after 2 statements.
public sealed class TripViewerReader(ApplicationDbContext db, ITripAccess access, ITripMoneyCards money, TimeProvider clock)
{
    // Owner + the first two joined viewers; the counts and stamps are uncorrelated subqueries (evaluated once).
    internal static readonly string StampSql = $"""
        SELECT x."UserId", x."Name", x."Username", x."IsOwner",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = @trip AND j."Status" = 'Joined') AS "ViewerCount",
               (SELECT count(*)::int FROM "TripMembers" w
                WHERE w."TripId" = @trip AND {LooksWaitingWhere}) AS "PendingInvites",
               COALESCE((SELECT s."Version" FROM "TripExpenseState" s WHERE s."TripId" = @trip), 0) AS "BalancesVersion",
               GREATEST(
                 (SELECT max(e."UpdatedAt") FROM "TripEnquiries" e WHERE e."TripId" = @trip),
                 (SELECT max(r."UpdatedAt") FROM "TaxiRequests" r WHERE r."TripId" = @trip AND r."TravellerUserId" = @owner),
                 (SELECT max(GREATEST(b."BookedAt", COALESCE(b."StartedAt", b."BookedAt"), COALESCE(b."CompletedAt", b."BookedAt"), COALESCE(b."CancelledAt", b."BookedAt")))
                  FROM "TaxiBookings" b WHERE b."TripId" = @trip)) AS "BookingAt"
        FROM (
          SELECT t."OwnerUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username"::text AS "Username", TRUE AS "IsOwner", NULL::timestamptz AS "JoinedAt"
          FROM "Trips" t JOIN "Users" u ON u."Id" = t."OwnerUserId"
          WHERE t."Id" = @trip
          UNION ALL
          (SELECT m."UserId", COALESCE(u."Name", ''), NULL::text, FALSE, m."JoinedAt"
           FROM "TripMembers" m JOIN "Users" u ON u."Id" = m."UserId"
           WHERE m."TripId" = @trip AND m."Status" = 'Joined'
           ORDER BY m."JoinedAt", m."UserId"
           LIMIT 2)
        ) x
        ORDER BY x."IsOwner" DESC, x."JoinedAt" NULLS FIRST, x."UserId"
        """;

    public async Task<SharingResult<TripViewResult>> GetAsync(Guid tripId, Guid callerId, string? ifNoneMatch, CancellationToken ct = default)
    {
        var info = await access.GetAsync(tripId, callerId, ct);
        if (info is null) return SharingResult<TripViewResult>.Fail(BuddyErrorTypes.TripNotFound);
        if (info.Level == TripAccessLevel.Former)
        {
            var standing = (await money.GetStandingsAsync([tripId], callerId, ct)).GetValueOrDefault(tripId);
            return SharingResult<TripViewResult>.Fail(BuddyErrorTypes.NotOnTrip, new NotOnTripDataDto
            {
                HasShare = standing?.HasShare ?? false,
                Title = info.Title,
                OwnerName = info.OwnerName,
                StartDate = info.StartDate,
                EndDate = info.EndDate,
                EndedHow = info.EndedHow,
            }, BuddyErrorTypes.NotOnTripViewStatus);
        }

        var stamp = await db.Database.SqlQueryRaw<StampRow>(StampSql, Id("trip", tripId), Id("owner", info.OwnerUserId)).ToListAsync(ct);
        if (stamp.Count == 0) return SharingResult<TripViewResult>.Fail(BuddyErrorTypes.TripNotFound);
        var head = stamp[0];
        var travellerCount = 1 + head.ViewerCount;
        var etag = TripSharingRules.ViewETag(info.UpdatedAt, head.BalancesVersion, travellerCount, head.BookingAt?.Ticks ?? 0);
        if (TripSharingRules.ETagMatches(ifNoneMatch, etag))
            return SharingResult<TripViewResult>.Success(new TripViewResult(null, etag, true));

        var trip = await ViewerTrip(db.Trips.Where(t => t.Id == tripId)).FirstOrDefaultAsync(ct);
        if (trip is null) return SharingResult<TripViewResult>.Fail(BuddyErrorTypes.TripNotFound);
        var enquiries = await ViewerEnquiries(db.TripEnquiries.Where(e => e.TripId == tripId)).ToListAsync(ct);
        var taxi = await TripTaxiRequestReads.ReadAsync(db, trip, ct);
        var card = (await money.GetCardsAsync([tripId], callerId, ct)).GetValueOrDefault(tripId);

        var view = TripViewerProjection.Build(new TripViewerSource(
            trip, TripPlanReader.Read(trip),
            info.Level == TripAccessLevel.Owner ? TripSharingRoles.Owner : TripSharingRoles.Viewer,
            info.OwnerName, head.Username, travellerCount, Chips(stamp),
            enquiries, taxi, card, TripSharingRules.TodayIst(clock.GetUtcNow().UtcDateTime)));
        return SharingResult<TripViewResult>.Success(new TripViewResult(view, etag, false));
    }

    internal static List<TravellerChipDto> Chips(IEnumerable<StampRow> rows)
        => rows.Take(3).Select(r => new TravellerChipDto { UserId = r.UserId, Name = r.Name, Initial = MoneyPeople.Initial(r.Name) }).ToList();

    internal sealed class StampRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public bool IsOwner { get; set; }
        public int ViewerCount { get; set; }
        public int PendingInvites { get; set; }
        public long BalancesVersion { get; set; }
        public DateTime? BookingAt { get; set; }
    }
}

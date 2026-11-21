using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

public static class SharedListStates
{
    public const string Upcoming = "upcoming";
    public const string Past = "past";

    public static bool IsValid(string? state) => state is Upcoming or Past;
}

// S15 (contract 2.2). First page: joined page, invites, former, enquiries, taxi summaries, money cards (2), counts = 8,
// + standings (4) only when former is not empty. A next page: joined page, enquiries, taxi summaries, money cards (2) = 5.
public sealed class TripSharedListReader(ApplicationDbContext db, ITripMoneyCards money, TimeProvider clock)
{
    private const string UpcomingSql = """(t."Status" <> 'Completed' AND (t."EndDate" IS NULL OR t."EndDate" >= @today))""";

    private const string JoinedPageSql = $"""
        SELECT m."Id" AS "MemberId", m."JoinedAt", t."Id" AS "TripId", t."OwnerUserId", t."Title", t."Status",
               t."CityKey", t."CityName", t."CityLatitude", t."CityLongitude", t."DistrictId", t."PlanJson"::text AS "PlanJson",
               t."DayCount", t."SpotCount", t."StartDate", t."EndDate", t."CreatedAt", t."UpdatedAt",
               COALESCE(o."Name", '') AS "OwnerName",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = t."Id" AND j."Status" = 'Joined') AS "ViewerCount"
        FROM "TripMembers" m
        JOIN "Trips" t ON t."Id" = m."TripId"
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        WHERE m."UserId" = @me AND m."Status" = 'Joined'
          AND {UpcomingSql} = @upcoming
          AND (NOT @hasCursor OR (m."JoinedAt", m."Id") < (@cursorAt, @cursorId))
        ORDER BY m."JoinedAt" DESC, m."Id" DESC
        LIMIT @take
        """;

    private static readonly string InvitesSql = $"""
        SELECT m."Id" AS "MemberId", m."InviteExpiresAt", t."Title", t."StartDate", t."EndDate", t."DayCount",
               jsonb_build_object('legs', t."PlanJson"->'legs')::text AS "PlanJson", t."CityName", COALESCE(o."Name", '') AS "OwnerName"
        FROM "TripMembers" m
        JOIN "Trips" t ON t."Id" = m."TripId"
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        WHERE m."UserId" = @me AND m."Status" = 'Invited' AND m."InviteExpiresAt" > now()
          AND NOT EXISTS (SELECT 1 FROM "UserBlocks" k
                          WHERE (k."BlockerId" = @me AND k."BlockedId" = t."OwnerUserId")
                             OR (k."BlockerId" = t."OwnerUserId" AND k."BlockedId" = @me))
        ORDER BY m."InvitedAt" DESC, m."Id" DESC
        LIMIT {BuddyLimits.SharedTripsPageMax}
        """;

    // Former trips that can still hold money for me (a share, a paid expense or a counted payment); hasShare decides after.
    private static readonly string FormerSql = $"""
        SELECT m."TripId", m."Status" AS "EndedHow", t."OwnerUserId", t."Title", t."StartDate", t."EndDate",
               COALESCE(o."Name", '') AS "OwnerName"
        FROM "TripMembers" m
        JOIN "Trips" t ON t."Id" = m."TripId"
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        WHERE m."UserId" = @me AND m."Status" IN ('Left', 'Removed')
          AND (EXISTS (SELECT 1 FROM "TripExpenseShares" s WHERE s."TripId" = m."TripId" AND s."UserId" = @me)
               OR EXISTS (SELECT 1 FROM "TripExpenses" e WHERE e."TripId" = m."TripId" AND e."DeletedAt" IS NULL AND e."PaidByUserId" = @me)
               OR EXISTS (SELECT 1 FROM "TripPayments" p WHERE p."TripId" = m."TripId" AND p."State" IN ('SaidPaid', 'Received')
                          AND (p."FromUserId" = @me OR p."ToUserId" = @me)))
        ORDER BY m."EndedAt" DESC NULLS LAST, m."Id" DESC
        LIMIT {BuddyLimits.SharedTripsPageMax}
        """;

    private const string CountsSql = $"""
        SELECT count(*) FILTER (WHERE x.up)::int AS "Upcoming", count(*) FILTER (WHERE NOT x.up)::int AS "Past"
        FROM (SELECT {UpcomingSql} AS up
              FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
              WHERE m."UserId" = @me AND m."Status" = 'Joined') x
        """;

    private const string NamesSql = """
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name" FROM "Users" u WHERE u."Id" = ANY(@ids)
        """;

    public async Task<TripSharedListDto> GetAsync(Guid callerId, string state, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = TripSharingRules.TodayIst(now);
        var page = await db.Database.SqlQueryRaw<JoinedRow>(JoinedPageSql,
            Id("me", callerId), Date("today", today), Bool("upcoming", state == SharedListStates.Upcoming),
            Bool("hasCursor", cursor is not null), Time("cursorAt", cursor?.At ?? DateTime.UnixEpoch), Id("cursorId", cursor?.Id ?? Guid.Empty),
            Int("take", limit + 1)).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var dto = new TripSharedListDto
        {
            NextCursor = KeysetCursor.EncodeLast(page, hasMore, r => r.JoinedAt, r => r.MemberId),
        };
        dto.Items = await ItemsAsync(page, callerId, ct);
        if (cursor is not null) return dto;

        var invites = await db.Database.SqlQueryRaw<InviteCardRow>(InvitesSql, Id("me", callerId)).ToListAsync(ct);
        dto.Invites = invites.Select(r =>
        {
            var cities = LegCities(r.PlanJson, r.CityName);
            return new TripInviteCardDto
            {
                MemberId = r.MemberId, Title = r.Title, OwnerName = r.OwnerName, StartDate = r.StartDate, EndDate = r.EndDate,
                DayCount = r.DayCount, CityCount = cities.Count, ExpiresAt = r.InviteExpiresAt,
            };
        }).ToList();

        var former = await db.Database.SqlQueryRaw<FormerRow>(FormerSql, Id("me", callerId)).ToListAsync(ct);
        var counts = (await db.Database.SqlQueryRaw<CountsRow>(CountsSql, Id("me", callerId), Date("today", today)).ToListAsync(ct)).FirstOrDefault();
        dto.Counts = new TripSharedCountsDto { Upcoming = counts?.Upcoming ?? 0, Past = counts?.Past ?? 0 };
        if (former.Count > 0) dto.Former = await FormerAsync(former, callerId, ct);
        return dto;
    }

    private async Task<List<TripSharedItemDto>> ItemsAsync(List<JoinedRow> page, Guid callerId, CancellationToken ct)
    {
        if (page.Count == 0) return [];
        var trips = page.Select(r => r.ToTrip()).ToList();
        var ids = trips.Select(t => t.Id).ToList();
        var enquiries = await ViewerEnquiries(db.TripEnquiries.Where(e => e.TripId != null && ids.Contains(e.TripId.Value))).ToListAsync(ct);
        var byTrip = enquiries.ToLookup(e => e.TripId!.Value);
        var taxis = await TripTaxiRequestReads.ReadManyAsync(db, trips, ct);
        var cards = await money.GetCardsAsync(ids, callerId, ct);
        return page.Zip(trips).Select(p =>
        {
            var (row, trip) = p;
            var plan = TripPlanReader.Read(trip);
            var rows = byTrip[trip.Id].ToList();
            var taxi = taxis.GetValueOrDefault(trip.Id);
            return new TripSharedItemDto
            {
                TripId = trip.Id,
                Title = trip.Title,
                Role = TripSharingRoles.Viewer,
                OwnerName = row.OwnerName,
                LegCities = plan.Legs is { Count: > 0 } ? plan.LegCities : [trip.CityName],
                DayCount = trip.DayCount,
                SpotCount = trip.SpotCount,
                StartDate = trip.StartDate,
                EndDate = trip.EndDate,
                TravellerCount = 1 + row.ViewerCount,
                ProgressPercent = TripViewerProjection.Progress(trip, plan, rows, taxi),
                StayState = TripPartWords.Card(TripViewerProjection.Stay(plan, trip, rows).State),
                TaxiState = TripPartWords.Card(TripViewerProjection.Taxi(rows, taxi).State),
                PlanState = "Saved",
                JoinedAt = row.JoinedAt,
                Expenses = TripViewerProjection.SharedCard(cards.GetValueOrDefault(trip.Id)),
            };
        }).ToList();
    }

    private async Task<List<TripFormerCardDto>> FormerAsync(List<FormerRow> former, Guid callerId, CancellationToken ct)
    {
        var standings = await money.GetStandingsAsync(former.Select(f => f.TripId).ToList(), callerId, ct);
        var shown = former.Where(f => standings.GetValueOrDefault(f.TripId)?.HasShare == true).ToList();
        var otherReceivers = shown
            .Select(f => standings[f.TripId].OwesToUserId)
            .Where((id, i) => id is not null && id != shown[i].OwnerUserId)
            .Select(id => id!.Value).Distinct().ToList();
        var names = otherReceivers.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Database.SqlQueryRaw<NameRow>(NamesSql, Ids("ids", otherReceivers)).ToListAsync(ct)).ToDictionary(n => n.UserId, n => n.Name);
        return shown.Select(f =>
        {
            var s = standings[f.TripId];
            var owesTo = s.OwesToUserId is { } to && to != f.OwnerUserId ? names.GetValueOrDefault(to, MoneyPeople.DeletedName) : f.OwnerName;
            return new TripFormerCardDto
            {
                TripId = f.TripId, Title = f.Title, OwnerName = f.OwnerName, StartDate = f.StartDate, EndDate = f.EndDate,
                EndedHow = f.EndedHow, MyNet = s.MyNet, OwesToName = s.MyNet < 0 ? owesTo : null, SharedCount = s.SharedCount,
            };
        }).ToList();
    }

    private sealed class JoinedRow
    {
        public Guid MemberId { get; set; }
        public DateTime JoinedAt { get; set; }
        public Guid TripId { get; set; }
        public Guid OwnerUserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long CityKey { get; set; }
        public string CityName { get; set; } = string.Empty;
        public double CityLatitude { get; set; }
        public double CityLongitude { get; set; }
        public Guid? DistrictId { get; set; }
        public string PlanJson { get; set; } = "{}";
        public int DayCount { get; set; }
        public int SpotCount { get; set; }
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public int ViewerCount { get; set; }

        // A read-only copy for the pure row rules; never attached to the context.
        public Trip ToTrip() => new()
        {
            Id = TripId, OwnerUserId = OwnerUserId, Title = Title, Status = Status, CityKey = CityKey, CityName = CityName,
            CityLatitude = CityLatitude, CityLongitude = CityLongitude, DistrictId = DistrictId, PlanJson = PlanJson,
            DayCount = DayCount, SpotCount = SpotCount, StartDate = StartDate, EndDate = EndDate, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
        };
    }

    private sealed class InviteCardRow
    {
        public Guid MemberId { get; set; }
        public DateTime InviteExpiresAt { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public int DayCount { get; set; }
        public string PlanJson { get; set; } = "{}";
        public string CityName { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
    }

    private sealed class FormerRow
    {
        public Guid TripId { get; set; }
        public string EndedHow { get; set; } = string.Empty;
        public Guid OwnerUserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public string OwnerName { get; set; } = string.Empty;
    }

    private sealed class CountsRow
    {
        public int Upcoming { get; set; }
        public int Past { get; set; }
    }

    private sealed class NameRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}

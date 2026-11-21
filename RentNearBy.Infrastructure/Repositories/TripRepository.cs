using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TripRepository(ApplicationDbContext context) : Repository<Trip>(context), ITripRepository
{
    public async Task<Trip?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId)
        => await _dbSet.FirstOrDefaultAsync(t => t.Id == id && t.OwnerUserId == ownerUserId);

    public async Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;

        var rows = await _dbSet.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId)
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();

        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(
        Guid ownerUserId, int page, int pageSize, bool past, DateOnly todayIst)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;

        var query = _dbSet.AsNoTracking().Where(t => t.OwnerUserId == ownerUserId);
        // Active: soonest first, undated last. Past: latest end first.
        var ordered = past
            ? query.Where(TripListRules.PastPredicate(todayIst))
                .OrderBy(t => t.EndDate == null).ThenByDescending(t => t.EndDate).ThenByDescending(t => t.UpdatedAt)
            : query.Where(TripListRules.ActivePredicate(todayIst))
                .OrderBy(t => t.StartDate == null).ThenBy(t => t.StartDate).ThenByDescending(t => t.UpdatedAt);

        var rows = await ordered
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();

        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<(int Active, int Past)> GetStateCountsAsync(Guid ownerUserId, DateOnly todayIst)
    {
        var mine = _dbSet.AsNoTracking().Where(t => t.OwnerUserId == ownerUserId);
        var past = await mine.Where(TripListRules.PastPredicate(todayIst)).CountAsync();
        var all = await mine.CountAsync();
        return (all - past, past);
    }

    // Only trips with a date reminder whose window is open (the date sets come from TripReminderRules.*OpenDates) and
    // whose key is not logged yet; keyset by Id. Keys mirror StartsInKey / TodayKey / MarkCompleteKey.
    public async Task<(IReadOnlyList<Trip> Items, Guid? Next)> GetDateReminderCandidatesAsync(
        DateOnly[] startsIn, DateOnly[] startsToday, DateOnly[] markComplete, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        if (startsIn.Length + startsToday.Length + markComplete.Length == 0) return (Array.Empty<Trip>(), null);
        var status = TripStatuses.Planned;
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT t."Id" AS "Value" FROM "Trips" t
                WHERE t."Status" = {status} AND t."MutedAt" IS NULL
                  AND ({after}::uuid IS NULL OR t."Id" > {after}::uuid)
                  AND ((t."StartDate" = ANY({startsIn}) AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                          WHERE p."DedupeKey" = 'd3:' || t."Id"::text || ':' || to_char(t."StartDate", 'YYYYMMDD')))
                    OR (t."StartDate" = ANY({startsToday}) AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                          WHERE p."DedupeKey" = 'd0:' || t."Id"::text || ':' || to_char(t."StartDate", 'YYYYMMDD')))
                    OR (t."EndDate" = ANY({markComplete}) AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                          WHERE p."DedupeKey" = 'done:' || t."Id"::text || ':' || to_char(t."EndDate", 'YYYYMMDD'))))
                ORDER BY t."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        return await LoadPageAsync(ids, size);
    }

    private async Task<(IReadOnlyList<Trip> Items, Guid? Next)> LoadPageAsync(List<Guid> ids, int size)
    {
        if (ids.Count == 0) return (Array.Empty<Trip>(), null);
        var trips = await _dbSet.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (trips.OrderBy(t => order[t.Id]).ToList(), ids.Count == size ? ids[^1] : (Guid?)null);
    }

    // One-city trips never reach the page, nor trips whose trv key for this date is already logged.
    public async Task<(IReadOnlyList<Trip> Items, Guid? Next)> GetTravelEveCandidatesAsync(DateOnly travelDate, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var status = TripStatuses.Planned;
        var keyPrefix = "trv:";
        var keySuffix = $":{travelDate:yyyyMMdd}";
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT t."Id" AS "Value" FROM "Trips" t
                WHERE t."Status" = {status} AND t."MutedAt" IS NULL
                  AND t."StartDate" IS NOT NULL AND t."EndDate" IS NOT NULL
                  AND t."StartDate" <= {travelDate} AND t."EndDate" >= {travelDate}
                  AND (CASE WHEN jsonb_typeof(t."PlanJson" -> 'legs') = 'array'
                            THEN jsonb_array_length(t."PlanJson" -> 'legs') ELSE 0 END) > 1
                  AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                                  WHERE p."DedupeKey" = {keyPrefix} || t."Id"::text || {keySuffix})
                  AND ({after}::uuid IS NULL OR t."Id" > {after}::uuid)
                ORDER BY t."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        if (ids.Count == 0) return (Array.Empty<Trip>(), null);

        var trips = await _dbSet.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (trips.OrderBy(t => order[t.Id]).ToList(), ids.Count == size ? ids[^1] : (Guid?)null);
    }

    // Trips with an open row (TripBookingStates.IsOpenForCompletion), an active taxi request (partial index
    // ux_taxirequests_active_trip, same IN list) or an active taxi booking never reach the page, so they cannot pin it.
    public static readonly string AutoCompleteCandidatesSql = $"""
        SELECT t."Id" AS "Value" FROM "Trips" t
        WHERE t."Status" = @planned AND t."EndDate" IS NOT NULL AND t."EndDate" <= @endBefore
          AND (@after::uuid IS NULL OR t."Id" > @after::uuid)
          AND NOT EXISTS (SELECT 1 FROM "TripEnquiries" e
                          WHERE e."TripId" = t."Id"
                            AND e."Status" IN ('Asked', 'Offered', 'Countered', 'FareSent'))
          AND NOT EXISTS (SELECT 1 FROM "TaxiRequests" r
                          WHERE r."TripId" = t."Id" AND r."Status" IN ({RequestStates.ActiveSqlList}))
          AND NOT EXISTS (SELECT 1 FROM "TaxiBookings" b
                          WHERE b."TravellerUserId" = t."OwnerUserId" AND b."TripId" = t."Id"
                            AND b."Status" IN ({BookingStates.ActiveSqlList}))
        ORDER BY t."Id"
        LIMIT @size
        """;

    public async Task<(IReadOnlyList<Trip> Items, Guid? Next)> GetAutoCompleteCandidatesAsync(DateOnly endBefore, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var ids = await _context.Database
            .SqlQueryRaw<Guid>(AutoCompleteCandidatesSql,
                new NpgsqlParameter("planned", TripStatuses.Planned),
                new NpgsqlParameter("endBefore", NpgsqlDbType.Date) { Value = endBefore },
                new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = after.HasValue ? after.Value : DBNull.Value },
                new NpgsqlParameter("size", size))
            .ToListAsync();
        return await LoadPageAsync(ids, size);
    }

    // The trip row lock orders this against a taxi request create (it locks the same row and refuses a Completed trip):
    // a request committed first is seen by the update's fresh snapshot, a later one finds the trip Completed.
    public async Task<int> TryCompleteAsync(Guid id, DateTime nowUtc)
    {
        await using var tx = _context.Database.CurrentTransaction == null ? await _context.Database.BeginTransactionAsync() : null;
        await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Trips\" WHERE \"Id\" = {id} FOR UPDATE");
        var updated = await CompleteIfNothingOpenAsync(id, nowUtc);
        if (tx != null) await tx.CommitAsync();
        return updated;
    }

    private async Task<int> CompleteIfNothingOpenAsync(Guid id, DateTime nowUtc)
        => await _dbSet
            .Where(t => t.Id == id && t.Status == TripStatuses.Planned)
            .Where(t => !_context.Set<TaxiRequest>().Any(r => r.TripId == t.Id
                && (r.Status == RequestStates.Open || r.Status == RequestStates.Collecting || r.Status == RequestStates.Selected
                    || r.Status == RequestStates.Booked || r.Status == RequestStates.InProgress)))
            .Where(t => !_context.Set<TaxiBooking>().Any(b => b.TravellerUserId == t.OwnerUserId && b.TripId == t.Id
                && (b.Status == BookingStates.Booked || b.Status == BookingStates.InProgress)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TripStatuses.Completed)
                .SetProperty(t => t.CompletedAt, nowUtc)
                .SetProperty(t => t.UpdatedAt, nowUtc));

    public async Task<TripCityInfo?> FindCitySnapshotAsync(Guid ownerUserId, long cityKey)
        => await _dbSet.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId && t.CityKey == cityKey)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new TripCityInfo
            {
                CityKey = t.CityKey,
                Name = t.CityName,
                Latitude = t.CityLatitude,
                Longitude = t.CityLongitude,
            })
            .FirstOrDefaultAsync();

    // Row city first; else a later city of one of the caller's trips (fallback path only, no index).
    public async Task<TripCityInfo?> FindLegSnapshotAsync(Guid ownerUserId, long cityKey)
    {
        if (await FindCitySnapshotAsync(ownerUserId, cityKey) is { } row) return row;

        var probe = $"{{\"legs\":[{{\"cityKey\":{cityKey}}}]}}";
        var json = (await _context.Database
                .SqlQuery<string>($"""
                    SELECT "PlanJson" AS "Value" FROM "Trips"
                    WHERE "OwnerUserId" = {ownerUserId} AND "PlanJson" @> {probe}::jsonb
                    ORDER BY "UpdatedAt" DESC
                    LIMIT 1
                    """)
                .ToListAsync())
            .FirstOrDefault();
        if (json == null) return null;

        var plan = TripPlanReader.Parse(json);
        plan.Normalize();
        var leg = plan.Legs!.FirstOrDefault(l => l.CityKey == cityKey);
        return leg == null
            ? null
            : new TripCityInfo { CityKey = leg.CityKey, Name = leg.CityName, Latitude = leg.Latitude, Longitude = leg.Longitude };
    }

    public async Task<Trip?> FindByClientIdAsync(Guid ownerUserId, Guid clientTripId)
        => await _dbSet.FirstOrDefaultAsync(t => t.OwnerUserId == ownerUserId && t.ClientTripId == clientTripId);

    public async Task<Dictionary<Guid, TripEnquiryTripView>> GetDatesAndOwnPlansAsync(IReadOnlyCollection<Guid> ids, Guid viewerUserId)
    {
        if (ids.Count == 0) return new Dictionary<Guid, TripEnquiryTripView>();
        var list = ids.Distinct().ToList();
        var rows = await _dbSet.AsNoTracking()
            .Where(t => list.Contains(t.Id))
            .Select(t => new
            {
                t.Id, t.StartDate, t.EndDate,
                PlanJson = t.OwnerUserId == viewerUserId ? t.PlanJson : null,
                t.CityKey, t.CityName, t.CityLatitude, t.CityLongitude, t.DistrictId,
            })
            .ToListAsync();
        return rows.ToDictionary(
            r => r.Id,
            r => new TripEnquiryTripView(r.Id, r.StartDate, r.EndDate, r.PlanJson, r.CityKey, r.CityName,
                (double)r.CityLatitude, (double)r.CityLongitude, r.DistrictId));
    }

    public async Task<List<Trip>> GetByIdsForOwnerAsync(IReadOnlyCollection<Guid> ids, Guid ownerUserId)
    {
        if (ids.Count == 0) return new List<Trip>();
        var list = ids.Distinct().ToList();
        return await _dbSet.AsNoTracking()
            .Where(t => list.Contains(t.Id) && t.OwnerUserId == ownerUserId)
            .ToListAsync();
    }
}

// The trip's taxi request and bookings of the new tables (contract section 4). The traveller of a request is the trip
// owner, so every read goes through the traveller indexes (ix_taxirequests_traveller, ix_taxibookings_traveller).
public static class TripTaxiRequestReads
{
    // Per trip: the active request, else the latest one; with its offer facts, its newest booking and whether it was rated.
    public static readonly string SummarySql = $"""
        SELECT r."Id" AS "RequestId", r."TripId", r."TravellerUserId", r."Status", r."EndReason", r."Version", r."QueueWaitStartedAt",
               EXISTS (SELECT 1 FROM "TaxiRequestOffers" o
                       WHERE o."RequestId" = r."Id" AND o."Status" = '{OfferStates.Ringing}') AS "HasRingingOffer",
               EXISTS (SELECT 1 FROM "TaxiRequestOffers" o
                       WHERE o."RequestId" = r."Id" AND o."Status" IN ('{OfferStates.Missed}', '{OfferStates.Declined}')) AS "AnyOfferMissedOrDeclined",
               b."Id" AS "BookingId", b."Status" AS "BookingStatus", b."DateFrom" AS "BookingDateFrom", b."DateTo" AS "BookingDateTo",
               b."DriverName", b."VehicleName", b."Seats", b."PickupCityName",
               (b."Id" IS NOT NULL AND EXISTS (SELECT 1 FROM "TaxiRatings" x WHERE x."BookingId" = b."Id")) AS "Rated"
        FROM (
            SELECT DISTINCT ON (q."TripId") q."Id", q."TripId", q."TravellerUserId", q."Status", q."EndReason", q."Version", q."QueueWaitStartedAt"
            FROM "TaxiRequests" q
            WHERE q."TravellerUserId" = ANY(@owners) AND q."TripId" = ANY(@trips)
            ORDER BY q."TripId", (q."Status" IN ({RequestStates.ActiveSqlList})) DESC, q."CreatedAt" DESC, q."Id" DESC
        ) r
        LEFT JOIN LATERAL (
            SELECT k."Id", k."Status", k."DateFrom", k."DateTo", k."DriverName", k."VehicleName", k."Seats", k."PickupCityName"
            FROM "TaxiBookings" k
            WHERE k."TravellerUserId" = r."TravellerUserId" AND k."RequestId" = r."Id"
            ORDER BY k."BookedAt" DESC, k."Id" DESC
            LIMIT 1
        ) b ON true
        """;

    public static async Task<TripTaxiRequestSummary?> ReadAsync(ApplicationDbContext db, Trip trip, CancellationToken ct = default)
        => (await ReadManyAsync(db, [trip], ct)).GetValueOrDefault(trip.Id);

    public static async Task<Dictionary<Guid, TripTaxiRequestSummary>> ReadManyAsync(
        ApplicationDbContext db, IReadOnlyCollection<Trip> trips, CancellationToken ct = default)
    {
        if (trips.Count == 0) return new Dictionary<Guid, TripTaxiRequestSummary>();
        var owners = trips.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().OwnerUserId);
        var rows = await db.Database
            .SqlQueryRaw<SummaryRow>(SummarySql,
                new NpgsqlParameter("owners", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = owners.Values.Distinct().ToArray() },
                new NpgsqlParameter("trips", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = owners.Keys.ToArray() })
            .ToListAsync(ct);
        return Pick(rows, owners);
    }

    // A row counts only for the trip's own owner (one query serves trips of several owners).
    public static Dictionary<Guid, TripTaxiRequestSummary> Pick(IEnumerable<SummaryRow> rows, IReadOnlyDictionary<Guid, Guid> ownerByTrip)
        => rows
            .Where(r => ownerByTrip.TryGetValue(r.TripId, out var owner) && owner == r.TravellerUserId)
            .ToDictionary(r => r.TripId, ToSummary);

    // Booked, in progress and completed taxis of the trip, oldest first (the summary lists them).
    public static async Task<List<TripTaxiBookingSummary>> BookingsAsync(ApplicationDbContext db, Trip trip, CancellationToken ct = default)
        => await db.Set<TaxiBooking>().AsNoTracking()
            .Where(b => b.TravellerUserId == trip.OwnerUserId && b.TripId == trip.Id
                && (b.Status == BookingStates.Booked || b.Status == BookingStates.InProgress || b.Status == BookingStates.Completed))
            .OrderBy(b => b.DateFrom).ThenBy(b => b.BookedAt)
            .Select(b => new TripTaxiBookingSummary(b.Id, b.Status, b.DateFrom, b.DateTo, b.DriverName, b.VehicleName, b.Seats, b.PickupCityName))
            .ToListAsync(ct);

    public static TripTaxiRequestSummary ToSummary(SummaryRow row)
    {
        var waitUntil = row.QueueWaitStartedAt?.AddSeconds(TaxiDispatchRules.QueueWaitSeconds);
        var view = TaxiRequestViewRules.Derive(row.Status, row.EndReason, row.AnyOfferMissedOrDeclined, row.HasRingingOffer, waitUntil, row.Rated);
        var booking = row.BookingId is { } id
            ? new TripTaxiBookingSummary(id, row.BookingStatus ?? string.Empty, row.BookingDateFrom ?? default, row.BookingDateTo ?? default,
                row.DriverName ?? string.Empty, row.VehicleName ?? string.Empty, row.Seats ?? 0, row.PickupCityName ?? string.Empty)
            : null;
        return new TripTaxiRequestSummary(row.RequestId, row.Status, view, row.Version, booking);
    }

    public sealed class SummaryRow
    {
        public Guid RequestId { get; set; }
        public Guid TripId { get; set; }
        public Guid TravellerUserId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? EndReason { get; set; }
        public long Version { get; set; }
        public DateTime? QueueWaitStartedAt { get; set; }
        public bool HasRingingOffer { get; set; }
        public bool AnyOfferMissedOrDeclined { get; set; }
        public Guid? BookingId { get; set; }
        public string? BookingStatus { get; set; }
        public DateOnly? BookingDateFrom { get; set; }
        public DateOnly? BookingDateTo { get; set; }
        public string? DriverName { get; set; }
        public string? VehicleName { get; set; }
        public short? Seats { get; set; }
        public string? PickupCityName { get; set; }
        public bool Rated { get; set; }
    }
}

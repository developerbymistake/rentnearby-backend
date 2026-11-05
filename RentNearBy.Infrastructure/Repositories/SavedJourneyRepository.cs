using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class SavedJourneyRepository(ApplicationDbContext context) : ISavedJourneyRepository
{
    private const double NearRoadMetres = 3000;

    private sealed record LegMatchRow(Guid TaxiId, Guid RouteId, int Fare);

    public async Task AddAsync(SavedJourney journey) => await context.Set<SavedJourney>().AddAsync(journey);

    public async Task<int> CountLiveAsync(Guid userId)
        => await context.Set<SavedJourney>().CountAsync(j => j.UserId == userId && !j.IsDeleted && j.Status != JourneyStatuses.Completed);

    public async Task<IReadOnlyList<SavedJourney>> GetLiveByUserAsync(Guid userId)
        => await context.Set<SavedJourney>().AsNoTracking()
            .Include(j => j.Legs)
            .Where(j => j.UserId == userId && !j.IsDeleted)
            .OrderByDescending(j => j.UpdatedAt)
            .Take(JourneyRules.MaxLive)
            .ToListAsync();

    public async Task<SavedJourney?> GetOwnedAsync(Guid id, Guid userId, bool tracked)
    {
        var query = context.Set<SavedJourney>().Include(j => j.Legs).Where(j => j.Id == id && j.UserId == userId && !j.IsDeleted);
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync();
    }

    // A completed journey is never reused: the same plan and date asked again is a new journey.
    public async Task<SavedJourney?> FindByPlanAsync(Guid userId, string planKey, DateOnly date)
        => await context.Set<SavedJourney>().AsNoTracking()
            .Include(j => j.Legs)
            .Where(j => j.UserId == userId && !j.IsDeleted && j.Status != JourneyStatuses.Completed
                && j.PlanKey == planKey && j.TravelDate == date)
            .OrderByDescending(j => j.UpdatedAt)
            .FirstOrDefaultAsync();

    public async Task<Dictionary<Guid, JourneyTaxiInfo>> LoadTaxisAsync(IReadOnlyCollection<Guid> taxiIds)
    {
        if (taxiIds.Count == 0) return new Dictionary<Guid, JourneyTaxiInfo>();
        var ids = taxiIds.Distinct().ToList();
        var rows = await context.TaxiListings.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.UserId,
                Name = t.User.Name,
                Vehicle = t.VehicleType != null ? t.VehicleType.Name : null,
                t.Seats,
                t.NumberPlate,
                City = t.City != null ? t.City.Name : null,
                t.TripLeadsReceived,
                t.TripLeadsAnsweredInApp,
                t.ContactPhone,
                t.WhatsAppNumber,
                IsPublic = !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable,
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.Id, r =>
        {
            var name = TripTextSafety.Clean(r.Name, 120);
            if (name.Length == 0) name = TripTextSafety.Clean(r.Vehicle, 120);
            if (name.Length == 0) name = "Taxi";
            return new JourneyTaxiInfo(
                r.Id, r.UserId, name, r.Vehicle, r.Seats, TaxiPlate.Format(r.NumberPlate), r.City,
                TaxiTripReplyLabels.Of(r.TripLeadsReceived, r.TripLeadsAnsweredInApp), r.ContactPhone, r.WhatsAppNumber, r.IsPublic);
        });
    }

    public async Task<Dictionary<Guid, TripEnquiry>> LoadEnquiriesAsync(IReadOnlyCollection<Guid> enquiryIds)
    {
        if (enquiryIds.Count == 0) return new Dictionary<Guid, TripEnquiry>();
        var ids = enquiryIds.Distinct().ToList();
        return await context.TripEnquiries.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
    }

    public async Task<IReadOnlyList<JourneyLegMatch>> MatchLegAsync(
        double fromLat, double fromLng, double toLat, double toLng, IReadOnlyCollection<Guid> excludeTaxiIds, int take)
    {
        var radius = TaxiLimits.MatchRadiusMeters;
        var exclude = excludeTaxiIds.ToArray();
        var rows = await context.Database.SqlQuery<LegMatchRow>($"""
            WITH q AS (
                SELECT ST_MakePoint({fromLng}::float8, {fromLat}::float8)::geography AS f,
                       ST_MakePoint({toLng}::float8, {toLat}::float8)::geography     AS t
            ),
            best AS (
                SELECT DISTINCT ON (r."TaxiListingId") r."TaxiListingId" AS "TaxiId", r."Id" AS "RouteId", r."Fare"
                FROM "TaxiRoutes" r CROSS JOIN q
                WHERE (ST_DWithin(r."FromPoint", q.f, {radius}::float8) AND ST_DWithin(r."ToPoint", q.t, {radius}::float8))
                   OR (ST_DWithin(r."FromPoint", q.t, {radius}::float8) AND ST_DWithin(r."ToPoint", q.f, {radius}::float8))
                ORDER BY r."TaxiListingId", r."Fare"
            )
            SELECT b."TaxiId", b."RouteId", b."Fare"
            FROM best b
            JOIN "TaxiListings" t ON t."Id" = b."TaxiId"
              AND t."Status" = 'Approved' AND t."IsAvailable" = TRUE AND t."IsDeleted" = FALSE
            WHERE NOT (t."Id" = ANY({exclude}))
            ORDER BY t."PriorityRank" DESC, b."Fare", t."Id"
            LIMIT {take}
            """).ToListAsync();
        return rows.Select(r => new JourneyLegMatch(r.TaxiId, r.RouteId, r.Fare)).ToList();
    }

    public async Task<JourneyLegMatch?> FindOnLegAsync(Guid taxiId, double fromLat, double fromLng, double toLat, double toLng)
    {
        var radius = TaxiLimits.MatchRadiusMeters;
        var direct = await context.Database.SqlQuery<LegMatchRow>($"""
            WITH q AS (
                SELECT ST_MakePoint({fromLng}::float8, {fromLat}::float8)::geography AS f,
                       ST_MakePoint({toLng}::float8, {toLat}::float8)::geography     AS t
            )
            SELECT r."TaxiListingId" AS "TaxiId", r."Id" AS "RouteId", r."Fare"
            FROM "TaxiRoutes" r CROSS JOIN q
            JOIN "TaxiListings" t ON t."Id" = r."TaxiListingId"
              AND t."Status" = 'Approved' AND t."IsAvailable" = TRUE AND t."IsDeleted" = FALSE
            WHERE r."TaxiListingId" = {taxiId}
              AND ((ST_DWithin(r."FromPoint", q.f, {radius}::float8) AND ST_DWithin(r."ToPoint", q.t, {radius}::float8))
                OR (ST_DWithin(r."FromPoint", q.t, {radius}::float8) AND ST_DWithin(r."ToPoint", q.f, {radius}::float8)))
            ORDER BY r."Fare"
            LIMIT 1
            """).ToListAsync();
        if (direct.Count > 0) return new JourneyLegMatch(direct[0].TaxiId, direct[0].RouteId, direct[0].Fare);

        var along = await (
            from r in context.TaxiRoutes.AsNoTracking()
            join t in context.TaxiListings.AsNoTracking() on r.TaxiListingId equals t.Id
            join g in context.Set<TaxiRouteGeometry>().AsNoTracking() on r.Id equals g.RouteId
            where t.Id == taxiId && !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable
                && g.Status == TaxiRouteGeometryStatuses.Ok && g.GeometryJson != null
            orderby r.Fare
            select new { RouteId = r.Id, r.Fare, g.GeometryJson }).ToListAsync();

        foreach (var row in along)
        {
            var line = TaxiRouteGeometryRules.ParseLine(row.GeometryJson);
            if (line.Count < 2) continue;
            if (TaxiRouteGeometryRules.DistanceToLineMetres(fromLat, fromLng, line) <= NearRoadMetres
                && TaxiRouteGeometryRules.DistanceToLineMetres(toLat, toLng, line) <= NearRoadMetres)
                return new JourneyLegMatch(taxiId, row.RouteId, PartFare(row.Fare, line, fromLat, fromLng, toLat, toLng));
        }
        return null;
    }

    // A stretch along the road is priced by its share of the road length (TaxiFare), not at the whole route's fare.
    internal static int PartFare(int routeFare, IReadOnlyList<(double Lat, double Lon)> line, double fromLat, double fromLng, double toLat, double toLng)
    {
        var points = line.Select(p => new TaxiWaysPoint(p.Lat, p.Lon)).ToList();
        var cumulative = TaxiWaysGeo.Cumulative(points);
        var (_, a) = TaxiWaysGeo.PointToPolyline(new TaxiWaysPoint(fromLat, fromLng), points, cumulative);
        var (_, b) = TaxiWaysGeo.PointToPolyline(new TaxiWaysPoint(toLat, toLng), points, cumulative);
        return TaxiFare.ForSpan(routeFare, TaxiFare.SpanOf(a, b));
    }

    // Same public rule as LoadTaxisAsync; a missing taxi row counts as not public. The work set shrinks as legs are
    // flipped and eve keys logged, and the Id keyset reaches every row, so nothing starves behind a fixed first page.
    public async Task<(List<SavedJourney> Items, Guid? Next)> GetWatchedPageAsync(DateOnly fromDate, DateOnly? eveDate, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var completed = JourneyStatuses.Completed;
        var approved = TaxiStatuses.Approved;
        var eveOn = eveDate != null;
        var eve = eveDate ?? fromDate;
        var eveSuffix = $":{eve:yyyyMMdd}";
        var ids = await context.Database
            .SqlQuery<Guid>($"""
                SELECT j."Id" AS "Value" FROM "SavedJourneys" j
                WHERE j."IsDeleted" = FALSE AND j."Status" <> {completed} AND j."TravelDate" >= {fromDate}
                  AND ({after}::uuid IS NULL OR j."Id" > {after}::uuid)
                  AND (EXISTS (SELECT 1 FROM "SavedJourneyLegs" l
                               LEFT JOIN "TaxiListings" t ON t."Id" = l."DriverTaxiId"
                               WHERE l."JourneyId" = j."Id"
                                 AND l."DriverAvailable" <> COALESCE(t."IsDeleted" = FALSE AND t."Status" = {approved} AND t."IsAvailable" = TRUE, FALSE))
                       OR ({eveOn} AND j."TravelDate" = {eve}
                           AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                                           WHERE p."DedupeKey" = {JourneyEveRules.KeyPrefix} || replace(j."Id"::text, '-', '') || {eveSuffix})))
                ORDER BY j."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        if (ids.Count == 0) return (new List<SavedJourney>(), null);

        var rows = await context.Set<SavedJourney>()
            .Include(j => j.Legs)
            .Where(j => ids.Contains(j.Id))
            .ToListAsync();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (rows.OrderBy(j => order[j.Id]).ToList(), ids.Count == size ? ids[^1] : (Guid?)null);
    }

    public async Task<List<SavedJourney>> GetOwnedByIdsAsync(IReadOnlyCollection<Guid> ids, Guid userId)
    {
        if (ids.Count == 0) return new List<SavedJourney>();
        var list = ids.Distinct().ToList();
        return await context.Set<SavedJourney>().AsNoTracking()
            .Include(j => j.Legs)
            .Where(j => list.Contains(j.Id) && j.UserId == userId && !j.IsDeleted)
            .ToListAsync();
    }

    public async Task<List<SavedJourney>> GetByTripAsync(Guid tripId, Guid userId)
        => await context.Set<SavedJourney>().AsNoTracking()
            .Include(j => j.Legs)
            .Where(j => j.TripId == tripId && j.UserId == userId && !j.IsDeleted)
            .OrderByDescending(j => j.UpdatedAt)
            .ToListAsync();

    public async Task<Dictionary<Guid, List<SavedJourney>>> GetByTripsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId)
    {
        if (tripIds.Count == 0) return new Dictionary<Guid, List<SavedJourney>>();
        var ids = tripIds.Distinct().ToList();
        var rows = await context.Set<SavedJourney>().AsNoTracking()
            .Include(j => j.Legs)
            .Where(j => j.TripId != null && ids.Contains(j.TripId.Value) && j.UserId == userId && !j.IsDeleted)
            .ToListAsync();
        return rows.GroupBy(j => j.TripId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task RelinkTripAsync(Guid tripId, Guid userId, IReadOnlyCollection<Guid> keepIds)
    {
        var keep = keepIds.Distinct().ToList();
        if (keep.Count > 0)
            await context.Set<SavedJourney>()
                .Where(j => keep.Contains(j.Id) && j.UserId == userId && !j.IsDeleted && j.TripId != tripId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.TripId, (Guid?)tripId));
        await context.Set<SavedJourney>()
            .Where(j => j.TripId == tripId && j.UserId == userId && !keep.Contains(j.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TripId, (Guid?)null));
    }

    public async Task SetTripAsync(Guid journeyId, Guid? tripId)
        => await context.Set<SavedJourney>()
            .Where(j => j.Id == journeyId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TripId, tripId));

    // Tracked: the caller may close them in the same unit of work.
    public async Task<List<TripEnquiry>> GetSegmentRowsAsync(Guid journeyId)
    {
        // ContextJson is jsonb: containment, not LIKE.
        var probe = $"{{\"journeyId\":\"{journeyId}\"}}";
        var rows = await context.TripEnquiries
            .FromSqlInterpolated($"""SELECT * FROM "TripEnquiries" WHERE "ContextJson" @> {probe}::jsonb""")
            .Where(e => e.TripId != null && e.Kind == TripEnquiryKinds.Taxi && e.Day != null)
            .ToListAsync();
        return rows.Where(e => TripEnquiryContexts.Read(e.ContextJson).JourneyId == journeyId).ToList();
    }

    public async Task UnlinkTripAsync(Guid tripId)
        => await context.Set<SavedJourney>()
            .Where(j => j.TripId == tripId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TripId, (Guid?)null));

    public Task<int> SaveChangesAsync() => context.SaveChangesAsync();
}

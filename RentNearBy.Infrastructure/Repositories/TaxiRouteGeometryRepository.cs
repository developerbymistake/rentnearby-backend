using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TaxiRouteGeometryRepository(ApplicationDbContext context) : ITaxiRouteGeometryRepository
{
    public async Task<IReadOnlyList<TaxiRouteGeometryWork>> GetDueAsync(int take, DateTime failedBeforeUtc)
    {
        // A route saved after its line was last checked is re-checked: same ends only touch the row.
        var rows = await (
            from r in context.TaxiRoutes.AsNoTracking()
            join t in context.TaxiListings.AsNoTracking() on r.TaxiListingId equals t.Id
            join g in context.Set<TaxiRouteGeometry>().AsNoTracking() on r.Id equals g.RouteId into gj
            from g in gj.DefaultIfEmpty()
            where !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable
                && (g == null
                    || g.Status == TaxiRouteGeometryStatuses.Pending
                    || r.UpdatedAt > g.UpdatedAt
                    || (g.Status == TaxiRouteGeometryStatuses.Failed
                        && g.Attempts < TaxiRouteGeometryRules.MaxAttempts && g.UpdatedAt < failedBeforeUtc))
            orderby r.UpdatedAt
            select new { r.Id, r.FromLatitude, r.FromLongitude, r.ToLatitude, r.ToLongitude, Existing = g })
            .Take(take)
            .ToListAsync();

        return rows.Select(x =>
        {
            double fromLat = (double)x.FromLatitude, fromLng = (double)x.FromLongitude;
            double toLat = (double)x.ToLatitude, toLng = (double)x.ToLongitude;
            return new TaxiRouteGeometryWork(
                x.Id, fromLat, fromLng, toLat, toLng, TaxiRouteGeometryRules.EndsHash(fromLat, fromLng, toLat, toLng), x.Existing);
        }).ToList();
    }

    public async Task<string?> FindContainingStateAsync(double lat, double lng)
    {
        var point = new Point(lng, lat) { SRID = 4326 };
        return await context.Districts.AsNoTracking()
            .Where(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point))
            .Select(d => d.StateName)
            .FirstOrDefaultAsync();
    }

    public async Task UpsertAsync(TaxiRouteGeometry row)
    {
        var set = context.Set<TaxiRouteGeometry>();
        var tracked = await set.FindAsync(row.RouteId);
        if (tracked == null)
        {
            await set.AddAsync(row);
        }
        else
        {
            tracked.EndsHash = row.EndsHash;
            tracked.Status = row.Status;
            tracked.GeometryJson = row.GeometryJson;
            tracked.DistanceKm = row.DistanceKm;
            tracked.Minutes = row.Minutes;
            tracked.Attempts = row.Attempts;
            tracked.UpdatedAt = row.UpdatedAt;
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}

using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// One projected query: exists + IsActive + ST_Contains(Boundary, pin) + BusinessOnboardingEnabled.
public static class BusinessOnboardingGate
{
    public static async Task<OnboardingCheck> CheckAsync(
        ApplicationDbContext db, Guid districtId, decimal latitude, decimal longitude, CancellationToken ct = default)
    {
        var point = new Point((double)longitude, (double)latitude) { SRID = 4326 };
        var row = await db.Districts.AsNoTracking()
            .Where(d => d.Id == districtId)
            .Select(d => new
            {
                d.IsActive,
                d.BusinessOnboardingEnabled,
                Inside = d.Boundary != null && d.Boundary.Contains(point),
            })
            .FirstOrDefaultAsync(ct);
        return BusinessOnboardingRules.Decide(row != null, row?.IsActive ?? false, row?.Inside ?? false, row?.BusinessOnboardingEnabled ?? false);
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// M2 (contract 2.4, AU-9, AU-21): the driver hub extras of GET /taxis/me. Fixed cost: the entitlement (1), hidden routes (1),
// active bookings (1), confirmed trip leads still to come (1, FB-51: the number the delete dialog warns with).
public sealed class TaxiHubReader(ApplicationDbContext db, IPlanEntitlementService entitlements)
{
    private static readonly string[] Active = [.. BookingStates.Active];

    public async Task FillAsync(TaxiOwnerDto dto, TaxiListing taxi, ICityResolver resolver, TimeSpan namesBudget, CancellationToken ct = default)
    {
        var limits = await entitlements.GetLimitsAsync(taxi.UserId, ListingKinds.Taxi);
        dto.HiddenRoutes = await HiddenRoutesAsync(db, taxi.Id, ct);
        await TaxiRouteCityKeys.FillNamesAsync(dto.HiddenRoutes, resolver, namesBudget);
        var bookings = await db.Set<TaxiBooking>().AsNoTracking()
            .CountAsync(b => b.DriverListingId == taxi.Id && Active.Contains(b.Status), ct);
        var leads = await ConfirmedLeadsAsync(db, taxi.Id, IstTime.TodayIst(DateTime.UtcNow), ct);
        Apply(dto, taxi, limits, bookings + leads);
    }

    // FB-51: confirmed taxi leads of the driver whose last day is today or later (ix_tripenquiries_listing_status). The hub
    // warning and the delete answer use this one count.
    public static Task<int> ConfirmedLeadsAsync(ApplicationDbContext db, Guid taxiId, DateOnly today, CancellationToken ct = default)
        => db.TripEnquiries.AsNoTracking().CountAsync(e => e.ListingId == taxiId && e.Kind == TripEnquiryKinds.Taxi
            && (e.Status == TripEnquiryStatuses.ConfirmedByBoth || e.Status == TripEnquiryStatuses.BookedByTraveller)
            && e.CheckOut >= today, ct);

    public static Task<List<TaxiRouteDto>> HiddenRoutesAsync(ApplicationDbContext db, Guid taxiId, CancellationToken ct = default)
        => db.TaxiRoutes.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TaxiListingId == taxiId && r.HiddenByPlan)
            .OrderBy(r => r.RouteIndex)
            .Select(RouteDto)
            .ToListAsync(ct);

    // The one route read model of the owner hub (same fields as DtoMappings.ToTaxiRouteDtos); a same-name copy, never a key write.
    public static readonly System.Linq.Expressions.Expression<Func<TaxiRoute, TaxiRouteDto>> RouteDto = r => new TaxiRouteDto
    {
        Id = r.Id, RouteIndex = r.RouteIndex, FromName = r.FromName, FromLabel = r.FromLabel,
        FromLatitude = r.FromLatitude, FromLongitude = r.FromLongitude, ToName = r.ToName, ToLabel = r.ToLabel,
        ToLatitude = r.ToLatitude, ToLongitude = r.ToLongitude, FromCityKey = r.FromCityKey, ToCityKey = r.ToCityKey,
        CityKeysChosen = r.CityKeysChosen, Fare = r.Fare, TripType = r.TripType,
    };

    public static void Apply(TaxiOwnerDto dto, TaxiListing taxi, PlanLimits limits, int activeBookings)
    {
        var meter = new MeterDto(PlanFeatureKeys.Routes, dto.Routes.Count, limits.Routes);
        dto.Meters = [meter];
        dto.RoutesMeter = meter;
        dto.ActiveBookingCount = activeBookings;
        dto.RingWeight = taxi.RingWeight;
        dto.RemindersOn = taxi.RemindersOn;
        dto.IsFeatured = taxi.IsFeatured;
        dto.PaymentEnabled = limits.PaymentEnabled;
        dto.CanUpgrade = limits.CanUpgrade;
        dto.ReportCount = dto.PendingReportCount;
    }
}

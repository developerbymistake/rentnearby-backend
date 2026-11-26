using System.Linq.Expressions;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// SPEC 9.2: a confirmed From app booking hides the taxi for the days it covers. One switch.
public static class TaxiAvailabilityRules
{
    public static readonly bool BookingHides = true;
}

// SPEC 2, the one availability rule of every public taxi list (contract 1.9). Point probes on PK TaxiDayOffs and
// ix_calendarentries_listing_dateto, inside the caller's single statement. Nobody writes these NOT EXISTS by hand elsewhere.
public static class TaxiAvailabilitySql
{
    public const string DayParameter = "@day";

    public static string FreeOn(string t, string day) => FreeOn(t, day, TaxiAvailabilityRules.BookingHides);

    public static string FreeOn(string t, string day, bool bookingHides)
    {
        var dayOff = $"""NOT EXISTS (SELECT 1 FROM "TaxiDayOffs" dof WHERE dof."TaxiListingId" = {t}."Id" AND dof."Date" = {day})""";
        if (!bookingHides) return dayOff;
        return dayOff + $"""
             AND NOT EXISTS (SELECT 1 FROM "CalendarEntries" ce WHERE ce."ListingKind" = '{ListingKinds.Taxi}' AND ce."ListingId" = {t}."Id" AND ce."Kind" = '{CalendarEntryKinds.Auto}' AND NOT ce."Cancelled" AND ce."DateTo" >= {day} AND ce."DateFrom" <= {day})
            """;
    }

    public static string PublicOn(string t, string day)
        => $"""{t}."Status" = '{TaxiStatuses.Approved}' AND {t}."IsAvailable" AND NOT {t}."IsDeleted" AND {FreeOn(t, day)}""";

    public static NpgsqlParameter Day(DateOnly day) => new("day", NpgsqlDbType.Date) { Value = day };
}

// LINQ twin of TaxiAvailabilitySql.FreeOn for the EF browse queries (same tables, same predicates).
public static class TaxiAvailability
{
    public static Expression<Func<TaxiListing, bool>> FreeOn(ApplicationDbContext db, DateOnly day)
        => FreeOn(db, day, TaxiAvailabilityRules.BookingHides);

    public static Expression<Func<TaxiListing, bool>> FreeOn(ApplicationDbContext db, DateOnly day, bool bookingHides)
    {
        if (!bookingHides)
            return t => !db.Set<TaxiDayOff>().Any(x => x.TaxiListingId == t.Id && x.Date == day);
        return t => !db.Set<TaxiDayOff>().Any(x => x.TaxiListingId == t.Id && x.Date == day)
            && !db.Set<CalendarEntry>().Any(c => c.ListingKind == ListingKinds.Taxi && c.ListingId == t.Id
                && c.Kind == CalendarEntryKinds.Auto && !c.Cancelled && c.DateTo >= day && c.DateFrom <= day);
    }
}

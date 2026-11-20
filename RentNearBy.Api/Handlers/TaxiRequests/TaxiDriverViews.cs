using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// Everything one driver offer DTO is built from. Traveller name and phone are only read when the offer may show them.
public sealed record DriverOfferSource(
    TaxiRequestOffer Offer,
    TaxiRequest Request,
    TaxiBooking? Booking,
    string? TravellerName,
    string? TravellerPhone,
    TaxiDateRange? Conflict);

// Keyset position in the Earlier bucket: (OfferedAt, Id), newest first.
public readonly record struct DriverOfferCursor(DateTime OfferedAt, Guid Id)
{
    public string Encode()
    {
        var raw = Encoding.ASCII.GetBytes(OfferedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "." + Id.ToString("N"));
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryParse(string? text, out DriverOfferCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text) || text.Length > 80) return false;
        try
        {
            var b = text.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(b)).Split('.');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out var id))
                return false;
            cursor = new DriverOfferCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// Driver side views of contract section 6: offers (D1, D2), bookings (D8), rating (D9), readiness (D10).
public static class TaxiDriverViews
{
    private static readonly string[] EarlierStates =
        [OfferStates.Missed, OfferStates.NotSelected, OfferStates.NotAgreed, OfferStates.Closed, OfferStates.Withdrawn];

    public static bool BookingActive(TaxiBooking? booking) => booking is not null && BookingStates.Active.Contains(booking.Status);

    // Spec 5.3: contact only after the driver accepted, and only while the booking is live.
    public static bool TravellerVisible(string offerState, bool bookingActive)
        => offerState is OfferStates.Accepted or OfferStates.Selected || (offerState == OfferStates.Confirmed && bookingActive);

    // Declined offers are never listed; a declined offer opened by id reads as history.
    public static string BucketOf(string offerState, bool bookingActive) => OfferBuckets.For(offerState, bookingActive) ?? OfferBuckets.Earlier;

    public static DriverOfferDto BuildOffer(DriverOfferSource s, bool withTrip)
    {
        var o = s.Offer;
        var r = s.Request;
        var active = BookingActive(s.Booking);
        var snapshot = TaxiTripSnapshot.FromJson(r.TripSnapshotJson);
        var cities = snapshot?.CityNames ?? [];
        var days = Math.Max(1, r.DateTo.DayNumber - r.DateFrom.DayNumber + 1);

        return new DriverOfferDto
        {
            OfferId = o.Id,
            RequestId = r.Id,
            BookingId = s.Booking?.Id,
            State = o.Status,
            EndReason = o.EndReason,
            Bucket = BucketOf(o.Status, active),
            ExpiresAt = o.Status == OfferStates.Ringing ? Utc(o.ExpiresAt) : null,
            ConfirmBy = o.Status == OfferStates.Selected && o.ConfirmBy is { } by ? Utc(by) : null,
            PickupName = r.PickupCityName ?? r.DropCityName ?? cities.FirstOrDefault() ?? string.Empty,
            Sections = new TaxiSectionsDto { Pickup = r.WantPickup, Sightseeing = r.WantSightseeing, Drop = r.WantDrop },
            SightseeingDayCount = CountItems(r.SightseeingDaysJson),
            DateFrom = r.DateFrom,
            DateTo = r.DateTo,
            DayCount = days,
            NightCount = days - 1,
            RouteLine = TaxiRingPayload.RouteLine(r.PickupCityName, cities),
            Seats = r.Seats,
            Travellers = r.Travellers,
            Traveller = TravellerVisible(o.Status, active)
                ? new DriverTravellerDto { Name = s.TravellerName ?? string.Empty, Phone = NullIfEmpty(s.TravellerPhone) }
                : null,
            DatesConflict = o.Status == OfferStates.Selected && s.Conflict is { } c ? new DriverDatesConflictDto { From = c.From, To = c.To } : null,
            Trip = withTrip && snapshot is not null ? TripOf(snapshot) : null,
        };
    }

    // `current` = New and Booked offers (first page only), `earlier` = one page of the Earlier bucket, already newest first.
    public static DriverOfferListDto BuildList(DateTime serverNow, IEnumerable<DriverOfferSource> current, IEnumerable<DriverOfferSource> earlier, string? nextCursor)
    {
        var listed = current.OrderByDescending(s => s.Offer.OfferedAt).ThenByDescending(s => s.Offer.Id)
            .Select(s => BuildOffer(s, withTrip: false)).Where(Listed).ToList();
        var fresh = listed.Where(d => d.Bucket == OfferBuckets.New).ToList();
        var booked = listed.Where(d => d.Bucket == OfferBuckets.Booked).OrderBy(d => d.DateFrom).ToList();
        var history = earlier.Select(s => BuildOffer(s, withTrip: false)).Where(d => Listed(d) && d.Bucket == OfferBuckets.Earlier);

        return new DriverOfferListDto
        {
            ServerNow = Utc(serverNow),
            Counts = new DriverOfferCountsDto { New = fresh.Count, Booked = booked.Count },
            NextCursor = nextCursor,
            Items = [.. fresh, .. booked, .. history],
        };

        static bool Listed(DriverOfferDto d) => d.State != OfferStates.Declined;
    }

    public static DriverBookingDto BuildBooking(TaxiBooking b, string? travellerPhone)
    {
        var active = BookingActive(b);
        return new DriverBookingDto
        {
            BookingId = b.Id,
            OfferId = b.OfferId,
            RequestId = b.RequestId,
            State = b.Status,
            DateFrom = b.DateFrom,
            DateTo = b.DateTo,
            PickupName = b.PickupCityName,
            TravellerName = NullIfEmpty(b.TravellerName),
            TravellerPhone = active ? NullIfEmpty(travellerPhone) : null,
            CanWithdraw = active,
            CanEnd = b.Status == BookingStates.InProgress,
        };
    }

    // `bookings` = the rows the window query returned (LoadBookingsAsync). Items: active first, then newest.
    // `occupied` = the active ranges among them (calendar marks).
    public static DriverBookingsDto BuildBookings(DateOnly from, DateOnly to, IEnumerable<TaxiBooking> bookings, IReadOnlyDictionary<Guid, string?> phones)
    {
        var touching = bookings.ToList();
        return new DriverBookingsDto
        {
            From = from,
            To = to,
            Occupied = touching.Where(BookingActive).OrderBy(b => b.DateFrom)
                .Select(b => new DriverOccupiedDto { BookingId = b.Id, From = b.DateFrom, To = b.DateTo }).ToList(),
            Items = touching.OrderByDescending(BookingActive).ThenByDescending(b => b.BookedAt)
                .Select(b => BuildBooking(b, phones.GetValueOrDefault(b.TravellerUserId))).ToList(),
        };
    }

    // Decision 17: no points, no rank, no traveller name.
    public static DriverRatingDto BuildRating(int ratingSum, int ratingCount, int ridesCompleted, IEnumerable<TaxiRating> latest)
        => new()
        {
            Average = TaxiBookingService.RatingAverage(ratingSum, ratingCount),
            RatingCount = ratingCount,
            RidesCompleted = ridesCompleted,
            Latest = latest.OrderByDescending(r => r.CreatedAt).Take(TaxiDispatchRules.MaxLatestRatings)
                .Select(r => new DriverRatingRowDto { Date = IstTime.TodayIst(r.CreatedAt), Stars = r.Stars }).ToList(),
        };

    public static bool MissedRings(int ringMissStreak) => ringMissStreak >= TaxiDispatchRules.RingMissLimit;

    // Contract section 6: ready = a device report with notifications on, a valid token, and under the missed rings limit.
    public static DriverReadinessDto BuildReadiness(TaxiListing? listing, bool hasValidToken, int routesWithoutCity)
    {
        if (listing is null) return new DriverReadinessDto { HasValidToken = hasValidToken };
        var missed = MissedRings(listing.RingMissStreak);
        return new DriverReadinessDto
        {
            Approved = !listing.IsDeleted && listing.Status == TaxiStatuses.Approved,
            Online = listing.IsAvailable,
            RingReady = (listing.RingReadyAt is null || listing.RingNotificationsOn) && hasValidToken && !missed,
            NotificationsOn = listing.RingNotificationsOn,
            HasValidToken = hasValidToken,
            MissedRings = missed,
            RoutesWithoutCity = routesWithoutCity,
        };
    }

    public static DriverTripDto TripOf(TaxiTripSnapshot s) => new()
    {
        Name = s.Name,
        Pickup = s.Pickup is { } p ? new DriverTripPickupDto { Name = p.Name, Date = p.Date } : null,
        Drop = s.Drop is { } d ? new DriverTripDropDto { Name = d.Name, SameAsPickup = d.SameAsPickup, Date = d.Date } : null,
        Cities = (s.Cities ?? []).Select(c => new DriverTripCityDto { Name = c.Name, Nights = c.Nights, From = c.From, To = c.To }).ToList(),
        Days = (s.Days ?? []).Select(d => new DriverTripDayDto
        {
            Day = d.Day, Date = d.Date, City = d.City, Taxi = d.Taxi, Km = d.Km, Minutes = d.Minutes, Places = [.. d.Places ?? []],
        }).ToList(),
    };

    // ---- reads ----

    public static async Task<DriverOfferDto?> LoadOfferAsync(ApplicationDbContext db, Guid offerId, bool withTrip, CancellationToken ct)
    {
        var offer = await db.Set<TaxiRequestOffer>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null) return null;
        var sources = await SourcesAsync(db, [offer], ct);
        return sources.Count == 0 ? null : BuildOffer(sources[0], withTrip);
    }

    public static async Task<DriverOfferListDto> LoadListAsync(ApplicationDbContext db, Guid driverUserId, DriverOfferCursor? cursor, DateTime now, CancellationToken ct)
    {
        var listings = await ListingIdsAsync(db, driverUserId, ct);
        if (listings.Length == 0) return BuildList(now, [], [], null);

        var current = new List<TaxiRequestOffer>();
        if (cursor is null)
        {
            current = await db.Set<TaxiRequestOffer>().AsNoTracking()
                .Where(o => listings.Contains(o.DriverListingId)
                    && (OfferStates.Live.Contains(o.Status)
                        || (o.Status == OfferStates.Confirmed
                            && db.Set<TaxiBooking>().Any(b => b.OfferId == o.Id && BookingStates.Active.Contains(b.Status)))))
                .ToListAsync(ct);
        }

        var page = TaxiDispatchRules.OfferListPageSize;
        var ids = await db.Database.SqlQueryRaw<Guid>(EarlierSql(cursor is not null), EarlierParameters(listings, cursor, page + 1)).ToListAsync(ct);
        var more = ids.Count > page;
        var pageIds = ids.Take(page).ToList();
        var loaded = pageIds.Count == 0 ? [] : await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => pageIds.Contains(o.Id)).ToListAsync(ct);
        var earlier = pageIds.Select(id => loaded.First(o => o.Id == id)).ToList();

        var sources = await SourcesAsync(db, [.. current, .. earlier], ct);
        var byId = sources.ToDictionary(s => s.Offer.Id);
        var next = more && earlier.Count > 0 ? new DriverOfferCursor(earlier[^1].OfferedAt, earlier[^1].Id).Encode() : null;
        return BuildList(now,
            current.Where(o => byId.ContainsKey(o.Id)).Select(o => byId[o.Id]),
            earlier.Where(o => byId.ContainsKey(o.Id)).Select(o => byId[o.Id]),
            next);
    }

    public static async Task<DriverBookingDto?> LoadBookingAsync(ApplicationDbContext db, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Set<TaxiBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null) return null;
        var phones = await PhonesAsync(db, BookingActive(booking) ? [booking.TravellerUserId] : [], ct);
        return BuildBooking(booking, phones.GetValueOrDefault(booking.TravellerUserId));
    }

    public static async Task<DriverBookingsDto> LoadBookingsAsync(ApplicationDbContext db, Guid driverUserId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var listings = await ListingIdsAsync(db, driverUserId, ct);
        var bookings = listings.Length == 0 ? [] : await db.Set<TaxiBooking>().AsNoTracking()
            .Where(b => listings.Contains(b.DriverListingId) && b.DateFrom <= to && b.DateTo >= from)
            .ToListAsync(ct);
        var phones = await PhonesAsync(db, bookings.Where(BookingActive).Select(b => b.TravellerUserId), ct);
        return BuildBookings(from, to, bookings, phones);
    }

    public static async Task<DriverRatingDto> LoadRatingAsync(ApplicationDbContext db, Guid driverUserId, CancellationToken ct)
    {
        var listing = await db.TaxiListings.AsNoTracking().Where(l => l.UserId == driverUserId && !l.IsDeleted)
            .Select(l => new { l.Id, l.RatingSum, l.RatingCount, l.RidesCompleted }).FirstOrDefaultAsync(ct);
        if (listing is null) return BuildRating(0, 0, 0, []);
        var latest = await db.Set<TaxiRating>().AsNoTracking().Where(r => r.DriverListingId == listing.Id)
            .OrderByDescending(r => r.CreatedAt).Take(TaxiDispatchRules.MaxLatestRatings).ToListAsync(ct);
        return BuildRating(listing.RatingSum, listing.RatingCount, listing.RidesCompleted, latest);
    }

    public static async Task<DriverReadinessDto> LoadReadinessAsync(ApplicationDbContext db, Guid driverUserId, CancellationToken ct)
    {
        var listing = await db.TaxiListings.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == driverUserId && !l.IsDeleted, ct);
        var hasToken = await db.DeviceTokens.AsNoTracking().AnyAsync(t => t.UserId == driverUserId && t.IsValid, ct);
        var unresolved = listing is null ? 0 : await db.TaxiRoutes.AsNoTracking()
            .CountAsync(r => r.TaxiListingId == listing.Id && (r.FromCityKey == null || r.ToCityKey == null), ct);
        return BuildReadiness(listing, hasToken, unresolved);
    }

    // Requests, bookings, contact (only where visible) and the date clash of Selected offers, in a few set reads.
    public static async Task<List<DriverOfferSource>> SourcesAsync(ApplicationDbContext db, IReadOnlyList<TaxiRequestOffer> offers, CancellationToken ct)
    {
        if (offers.Count == 0) return [];
        var requestIds = offers.Select(o => o.RequestId).Distinct().ToList();
        var offerIds = offers.Select(o => o.Id).ToList();
        var requests = await db.Set<TaxiRequest>().AsNoTracking().Where(r => requestIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var bookings = (await db.Set<TaxiBooking>().AsNoTracking().Where(b => offerIds.Contains(b.OfferId)).ToListAsync(ct))
            .GroupBy(b => b.OfferId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.BookedAt).First());

        var visible = offers.Where(o => requests.ContainsKey(o.RequestId)
            && TravellerVisible(o.Status, BookingActive(bookings.GetValueOrDefault(o.Id)))).ToList();
        var travellerIds = visible.Select(o => requests[o.RequestId].TravellerUserId).Distinct().ToList();
        var users = travellerIds.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => travellerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.PhoneNumber }).ToListAsync(ct);

        var result = new List<DriverOfferSource>(offers.Count);
        foreach (var o in offers)
        {
            if (!requests.TryGetValue(o.RequestId, out var r)) continue;
            var booking = bookings.GetValueOrDefault(o.Id);
            string? name = null, phone = null;
            if (visible.Contains(o))
            {
                var u = users.FirstOrDefault(x => x.Id == r.TravellerUserId);
                name = NullIfEmpty(booking?.TravellerName) ?? u?.Name;
                phone = u?.PhoneNumber;
            }
            var conflict = o.Status == OfferStates.Selected
                ? await DriverCalendar.ConflictForAsync(db, o.DriverListingId, r.DateFrom, r.DateTo, ct)
                : null;
            result.Add(new DriverOfferSource(o, r, booking, name, phone, conflict));
        }
        return result;
    }

    public static string EarlierSql(bool afterCursor) => $"""
        SELECT o."Id" AS "Value" FROM "TaxiRequestOffers" o
        WHERE o."DriverListingId" = ANY(@listings)
          AND (o."Status" IN ({string.Join(", ", EarlierStates.Select(s => $"'{s}'"))})
               OR (o."Status" = '{OfferStates.Confirmed}' AND NOT EXISTS (
                   SELECT 1 FROM "TaxiBookings" b WHERE b."OfferId" = o."Id" AND b."Status" IN ({BookingStates.ActiveSqlList}))))
          {(afterCursor ? "AND (o.\"OfferedAt\", o.\"Id\") < (@at, @id)" : "")}
        ORDER BY o."OfferedAt" DESC, o."Id" DESC
        LIMIT @take
        """;

    private static object[] EarlierParameters(Guid[] listings, DriverOfferCursor? cursor, int take)
    {
        var list = new List<object>
        {
            new NpgsqlParameter("listings", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = listings },
            new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = take },
        };
        if (cursor is { } c)
        {
            list.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(c.OfferedAt, DateTimeKind.Utc) });
            list.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = c.Id });
        }
        return [.. list];
    }

    // Every listing the driver ever had: offers and bookings keep the listing id they were made with.
    private static async Task<Guid[]> ListingIdsAsync(ApplicationDbContext db, Guid driverUserId, CancellationToken ct)
        => await db.TaxiListings.AsNoTracking().Where(l => l.UserId == driverUserId).Select(l => l.Id).ToArrayAsync(ct);

    private static async Task<Dictionary<Guid, string?>> PhonesAsync(ApplicationDbContext db, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (string?)u.PhoneNumber, ct);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int CountItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// M1 (contract 2.4, AU-5, AU-9, AU-21): the owner hub extras of GET /stays/me and the live-edit answers. Fixed cost whatever the
// row counts: the entitlement (1), hidden photos (1), hidden rooms (1) and one facts statement (bookings, calendar row, special
// prices row, tonight's rules).
public sealed class StayHubReader(ApplicationDbContext db, IPlanEntitlementService entitlements, TimeProvider clock)
{
    private sealed class FactsRow
    {
        public int Confirmed { get; set; }
        public int MonthCount { get; set; }
        public DateOnly? NextDate { get; set; }
        public string? NextLabel { get; set; }
        public int RuleCount { get; set; }
        public string? FirstLabel { get; set; }
        public DateOnly? FirstFrom { get; set; }
        public DateOnly? FirstTo { get; set; }
        public string? FirstDirection { get; set; }
        public int? FirstPercent { get; set; }
        public string? TodayRules { get; set; }
    }

    // Visible manual rows: the CalendarEntries-limit soonest active ones by (DateTo, Id), and every past one; Auto rows always.
    private const string Visible = """
        (c."Kind" = 'Auto' OR c."DateTo" < @today OR c."Id" IN (
            SELECT m."Id" FROM "CalendarEntries" m
            WHERE m."ListingKind" = 'Stay' AND m."ListingId" = @l AND m."Kind" = 'Manual' AND m."DateTo" >= @today
            ORDER BY m."DateTo", m."Id" LIMIT @lim))
        """;

    public static readonly string FactsSql = $"""
        SELECT
          (SELECT count(*) FROM "TripEnquiries" e WHERE e."OwnerUserId" = @me AND e."Kind" = '{TripEnquiryKinds.Stay}'
              AND e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND e."CheckOut" >= @today)::int AS "Confirmed",
          (SELECT count(*) FROM "CalendarEntries" c WHERE c."ListingKind" = 'Stay' AND c."ListingId" = @l
              AND c."DateFrom" >= @m1 AND c."DateFrom" < @m2 AND NOT c."Cancelled" AND {Visible})::int AS "MonthCount",
          n."DateFrom" AS "NextDate", n."Label" AS "NextLabel",
          (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = @l AND sp."DateTo" >= @today)::int AS "RuleCount",
          f."LabelCopy" AS "FirstLabel", f."DateFrom" AS "FirstFrom", f."DateTo" AS "FirstTo", f."Direction" AS "FirstDirection",
          f."Percent"::int AS "FirstPercent",
          (SELECT json_agg(json_build_object('from', t."DateFrom", 'to', t."DateTo", 'lower', t."Direction" = 'Lower', 'percent', t."Percent"))
             FROM "StaySpecialPrices" t WHERE t."StayListingId" = @l AND t."DateFrom" <= @today AND t."DateTo" >= @today
               AND NOT t."IsPaused" AND NOT t."PausedByPlan")::text AS "TodayRules"
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT c."DateFrom", c."Label" FROM "CalendarEntries" c
            WHERE c."ListingKind" = 'Stay' AND c."ListingId" = @l AND c."DateFrom" >= @today AND NOT c."Cancelled" AND {Visible}
            ORDER BY c."DateFrom", c."Id" LIMIT 1) n ON true
        LEFT JOIN LATERAL (
            SELECT sp."LabelCopy", sp."DateFrom", sp."DateTo", sp."Direction", sp."Percent" FROM "StaySpecialPrices" sp
            WHERE sp."StayListingId" = @l AND sp."DateTo" >= @today
            ORDER BY sp."DateFrom", sp."Id" LIMIT 1) f ON true
        """;

    public async Task FillAsync(StayOwnerDto dto, StayListing stay, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var limits = await entitlements.GetLimitsAsync(stay.UserId, ListingKinds.Stay);

        dto.HiddenPhotos = await db.StayPhotos.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.StayListingId == stay.Id && p.HiddenByPlan)
            .OrderBy(p => p.PhotoOrder).ThenBy(p => p.Id)
            .Select(p => new HiddenPhotoDto(p.Id, p.PhotoUrl, p.PhotoOrder))
            .ToListAsync(ct);
        dto.HiddenRooms = await db.StayRooms.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.StayListingId == stay.Id && r.HiddenByPlan)
            .OrderBy(r => r.RoomIndex)
            .Select(r => new StayRoomDto
            {
                Id = r.Id, RoomIndex = r.RoomIndex, PricePerNight = r.PricePerNight, MaxGuests = r.MaxGuests,
                RoomName = r.RoomName, Meals = r.Meals, Floor = r.Floor,
            })
            .ToListAsync(ct);

        var month1 = new DateOnly(today.Year, today.Month, 1);
        var facts = (await db.Database.SqlQueryRaw<FactsRow>(FactsSql,
            new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = stay.UserId },
            new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = stay.Id },
            new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today },
            new NpgsqlParameter("m1", NpgsqlDbType.Date) { Value = month1 },
            new NpgsqlParameter("m2", NpgsqlDbType.Date) { Value = month1.AddMonths(1) },
            new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = limits.CalendarEntries }).ToListAsync(ct))[0];

        Apply(dto, stay, limits, facts.Confirmed, today, now);
        dto.CalendarRow = new StayCalendarRowDto(facts.MonthCount,
            facts.NextDate is { } d ? new StayCalendarRowNextDto(d, facts.NextLabel ?? string.Empty) : null);
        dto.SpecialPricesRow = new StaySpecialPricesRowDto(facts.RuleCount, limits.SpecialPrices,
            facts.FirstFrom is { } ff && facts.FirstTo is { } ft
                ? new StaySpecialPriceFirstDto(facts.FirstLabel, ff, ft, facts.FirstDirection ?? string.Empty, facts.FirstPercent ?? 0)
                : null);
        var rules = ParseRules(facts.TodayRules);
        foreach (var room in dto.Rooms) room.PriceToday = StaySpecialPriceMath.PriceOn(room.PricePerNight, today, rules);
    }

    // The fields that need no query (also used by the write answers that skip the hub statements).
    public static void Apply(StayOwnerDto dto, StayListing stay, PlanLimits limits, int confirmedBookings, DateOnly today, DateTime nowUtc)
    {
        dto.Meters =
        [
            new MeterDto(PlanFeatureKeys.Photos, dto.Photos.Count, limits.Photos),
            new MeterDto(PlanFeatureKeys.RoomTypes, dto.Rooms.Count, limits.RoomTypes),
        ];
        dto.PaymentEnabled = limits.PaymentEnabled;
        dto.CanUpgrade = limits.CanUpgrade;
        dto.ConfirmedBookingCount = confirmedBookings;
        dto.RemindersOn = stay.RemindersOn;
        dto.IsFeatured = stay.IsFeatured;
        dto.ReportCount = dto.PendingReportCount;
        dto.PendingEdit = PendingOf(stay);
        dto.LastEditResult = StayReviewFields.StripVisible(stay.PendingEditResult, stay.PendingEditResultAt, stay.PendingEditJson != null, nowUtc)
            ? new StayEditResultDto(stay.PendingEditResult!, stay.PendingEditRejectReason, stay.PendingEditResultAt!.Value, [])
            : null;
    }

    public static StayPendingEditDto? PendingOf(StayListing stay)
    {
        var pending = StayPendingEditJson.Read(stay.PendingEditJson);
        if (pending == null || stay.PendingEditAt is not { } at) return null;
        var a = pending.Asked;
        var f = pending.Fields;
        var fields = new StayPendingEditFieldsDto
        {
            Name = f.Contains(StayReviewFields.Name) ? a.Name : null,
            RegistrationTypeName = f.Contains(StayReviewFields.RegistrationType) ? a.RegistrationTypeName : null,
            RegistrationOtherName = f.Contains(StayReviewFields.RegistrationOtherName) ? a.RegistrationOtherName : null,
            RegistrationNumberChanged = f.Contains(StayReviewFields.RegistrationNumber) ? true : null,
            GstinChanged = f.Contains(StayReviewFields.Gstin) ? true : null,
            Address = f.Contains(StayReviewFields.Address) ? a.Address : null,
            DistrictName = f.Contains(StayReviewFields.District) ? a.DistrictName : null,
            CityName = f.Contains(StayReviewFields.City) ? a.CityName : null,
            Latitude = f.Contains(StayReviewFields.Location) ? a.Latitude : null,
            Longitude = f.Contains(StayReviewFields.Location) ? a.Longitude : null,
        };
        return new StayPendingEditDto(fields, f, at);
    }

    internal static IReadOnlyList<PriceRule> ParseRules(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<PriceRule>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!DateOnly.TryParse(e.GetProperty("from").GetString(), CultureInfo.InvariantCulture, out var from)) continue;
                if (!DateOnly.TryParse(e.GetProperty("to").GetString(), CultureInfo.InvariantCulture, out var to)) continue;
                list.Add(new PriceRule(from, to, e.GetProperty("lower").GetBoolean(), e.GetProperty("percent").GetInt32()));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return [];
        }
    }
}

// PendingEditJson read / write: one shape, defensive on read (a bad value reads as no pending change).
public static class StayPendingEditJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write(StayPendingEdit edit) => JsonSerializer.Serialize(edit, Options);

    public static StayPendingEdit? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var edit = JsonSerializer.Deserialize<StayPendingEdit>(json, Options);
            return edit is { Fields: not null, Asked: not null } && edit.Fields.Count > 0 ? edit : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

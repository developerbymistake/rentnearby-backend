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
// row counts: the entitlement (1, skipped when the write passes its limits) and ONE facts statement (bookings, calendar row,
// special prices row, tonight's rules, and since FB-30 the hidden photos and hidden rooms as two json columns).
public sealed class StayHubReader(ApplicationDbContext db, IPlanEntitlementService entitlements, TimeProvider clock)
{
    private sealed class FactsRow
    {
        public int Confirmed { get; set; }
        public int MonthCount { get; set; }
        public DateOnly? NextDate { get; set; }
        public string? NextLabel { get; set; }
        public bool NextTravellerRemoved { get; set; }
        public int RuleCount { get; set; }
        public string? FirstLabel { get; set; }
        public DateOnly? FirstFrom { get; set; }
        public DateOnly? FirstTo { get; set; }
        public string? FirstDirection { get; set; }
        public int? FirstPercent { get; set; }
        public string? TodayRules { get; set; }
        public string? HiddenPhotos { get; set; }
        public string? HiddenRooms { get; set; }
    }

    private sealed record HiddenPhotoJson(Guid Id, string Url, int PhotoOrder);

    private sealed record HiddenRoomJson(Guid Id, short RoomIndex, int PricePerNight, short MaxGuests, string RoomName, string Meals, short? Floor);

    private static readonly JsonSerializerOptions HiddenJson = new(JsonSerializerDefaults.Web);

    // Visible manual rows: the CalendarEntries-limit active ones added last (CreatedAt DESC, Id), and every past one; Auto rows always.
    private const string Visible = """
        (c."Kind" = 'Auto' OR c."DateTo" < @today OR c."Id" IN (
            SELECT m."Id" FROM "CalendarEntries" m
            WHERE m."ListingKind" = 'Stay' AND m."ListingId" = @l AND m."Kind" = 'Manual' AND m."DateTo" >= @today
            ORDER BY m."CreatedAt" DESC, m."Id" LIMIT @lim))
        """;

    public static readonly string FactsSql = $"""
        SELECT
          (SELECT count(*) FROM "TripEnquiries" e WHERE e."OwnerUserId" = @me AND e."Kind" = '{TripEnquiryKinds.Stay}'
              AND e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND e."CheckOut" >= @today)::int AS "Confirmed",
          (SELECT count(*) FROM "CalendarEntries" c WHERE c."ListingKind" = 'Stay' AND c."ListingId" = @l
              AND c."DateFrom" >= @m1 AND c."DateFrom" < @m2 AND NOT c."Cancelled" AND {Visible})::int AS "MonthCount",
          n."DateFrom" AS "NextDate", n."Label" AS "NextLabel", COALESCE(n."TravellerRemoved", false) AS "NextTravellerRemoved",
          (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = @l AND sp."DateTo" >= @today)::int AS "RuleCount",
          f."LabelCopy" AS "FirstLabel", f."DateFrom" AS "FirstFrom", f."DateTo" AS "FirstTo", f."Direction" AS "FirstDirection",
          f."Percent"::int AS "FirstPercent",
          (SELECT json_agg(json_build_object('from', t."DateFrom", 'to', t."DateTo", 'lower', t."Direction" = 'Lower', 'percent', t."Percent"))
             FROM "StaySpecialPrices" t WHERE t."StayListingId" = @l AND t."DateFrom" <= @today AND t."DateTo" >= @today
               AND NOT t."IsPaused" AND NOT t."PausedByPlan")::text AS "TodayRules",
          (SELECT json_agg(json_build_object('id', hp."Id", 'url', hp."PhotoUrl", 'photoOrder', hp."PhotoOrder") ORDER BY hp."PhotoOrder", hp."Id")
             FROM "StayPhotos" hp WHERE hp."StayListingId" = @l AND hp."HiddenByPlan")::text AS "HiddenPhotos",
          (SELECT json_agg(json_build_object('id', hr."Id", 'roomIndex', hr."RoomIndex", 'pricePerNight', hr."PricePerNight",
                    'maxGuests', hr."MaxGuests", 'roomName', hr."RoomName", 'meals', hr."Meals", 'floor', hr."Floor") ORDER BY hr."RoomIndex")
             FROM "StayRooms" hr WHERE hr."StayListingId" = @l AND hr."HiddenByPlan")::text AS "HiddenRooms"
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT c."DateFrom", c."Label", (c."Kind" = 'Auto' AND c."TravellerUserId" IS NULL) AS "TravellerRemoved" FROM "CalendarEntries" c
            WHERE c."ListingKind" = 'Stay' AND c."ListingId" = @l AND c."DateFrom" >= @today AND NOT c."Cancelled" AND {Visible}
            ORDER BY c."DateFrom", c."Id" LIMIT 1) n ON true
        LEFT JOIN LATERAL (
            SELECT sp."LabelCopy", sp."DateFrom", sp."DateTo", sp."Direction", sp."Percent" FROM "StaySpecialPrices" sp
            WHERE sp."StayListingId" = @l AND sp."DateTo" >= @today
            ORDER BY sp."DateFrom", sp."Id" LIMIT 1) f ON true
        """;

    // FB-63: a write that already read the limits under the stay lock passes them, so the answer costs no second entitlement read.
    public async Task FillAsync(StayOwnerDto dto, StayListing stay, PlanLimits? knownLimits = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var limits = knownLimits ?? await entitlements.GetLimitsAsync(stay.UserId, ListingKinds.Stay);

        var month1 = new DateOnly(today.Year, today.Month, 1);
        var facts = (await db.Database.SqlQueryRaw<FactsRow>(FactsSql,
            new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = stay.UserId },
            new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = stay.Id },
            new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today },
            new NpgsqlParameter("m1", NpgsqlDbType.Date) { Value = month1 },
            new NpgsqlParameter("m2", NpgsqlDbType.Date) { Value = month1.AddMonths(1) },
            new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = limits.CalendarEntries }).ToListAsync(ct))[0];

        dto.HiddenPhotos = ParseHiddenPhotos(facts.HiddenPhotos);
        dto.HiddenRooms = ParseHiddenRooms(facts.HiddenRooms);
        Apply(dto, stay, limits, facts.Confirmed, today, now);
        dto.CalendarRow = new StayCalendarRowDto(facts.MonthCount,
            facts.NextDate is { } d ? new StayCalendarRowNextDto(d, facts.NextLabel ?? string.Empty, facts.NextTravellerRemoved) : null);
        dto.SpecialPricesRow = new StaySpecialPricesRowDto(facts.RuleCount, limits.SpecialPrices,
            facts.FirstFrom is { } ff && facts.FirstTo is { } ft
                ? new StaySpecialPriceFirstDto(facts.FirstLabel, ff, ft, facts.FirstDirection ?? string.Empty, facts.FirstPercent ?? 0)
                : null);
        var rules = ParseRules(facts.TodayRules);
        foreach (var room in dto.Rooms) room.PriceToday = StaySpecialPriceMath.PriceOn(room.PricePerNight, today, rules);
    }

    internal static List<HiddenPhotoDto> ParseHiddenPhotos(string? json)
        => json == null ? [] : (JsonSerializer.Deserialize<List<HiddenPhotoJson>>(json, HiddenJson) ?? [])
            .Select(p => new HiddenPhotoDto(p.Id, p.Url, p.PhotoOrder)).ToList();

    internal static List<StayRoomDto> ParseHiddenRooms(string? json)
        => json == null ? [] : (JsonSerializer.Deserialize<List<HiddenRoomJson>>(json, HiddenJson) ?? [])
            .Select(r => new StayRoomDto
            {
                Id = r.Id, RoomIndex = r.RoomIndex, PricePerNight = r.PricePerNight, MaxGuests = r.MaxGuests,
                RoomName = r.RoomName, Meals = r.Meals, Floor = r.Floor,
            }).ToList();

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
        dto.EnquiryRingOn = stay.EnquiryRingOn;
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

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// GET /owner/home for a Taxi Driver (contract 2.6 H1, AU-19, AU-23). At most 6 statements, the same for 3 and 30 rows:
// (1) owner context with the listing, readiness, hidden routes, reports, token, notices and payment dates; (2) the work numbers
// (his selected offer, rings, leads, weekly rings and accepts, trip in progress, visible routes, the AddRoutes example);
// (3) upcoming; (4) weekly; (5) festival; (6) plan card extras (skipped unless Approved in an active area).
public sealed class TaxiDriverHomeReader(ApplicationDbContext db, IPlanCatalogCache catalog, IPaymentFeatureReader payment, TimeProvider clock)
{
    internal static readonly string ContextSql = $"""
        SELECT l."Id" AS "ListingId", l."Status", l."DistrictId", d."StateName", l."RemindersOn",
               NULL::date AS "SpecialPriceSavesDay", 0::smallint AS "SpecialPriceSavesCount", l."PlanVisibleSavesDay", l."PlanVisibleSavesCount",
               up."Id" AS "UserPlanId", up."PlanId", up."Source", up."StartsAt", up."EndsAt", up."PlaySubscriptionId",
               own."Name" AS "DriverName", vt."Name" AS "VehicleName", l."Seats", l."NumberPlate", c."Name" AS "CityName",
               l."IsAvailable" AS "SwitchOn", l."IsFeatured", l."EverApproved", l."RejectedReason",
               l."RingReadyAt", l."RingMissStreak", l."LocalDayRate", l."RatingSum", l."RatingCount", l."RingWeight",
               COALESCE(d."IsActive", false) AS "DistrictActive",
               (SELECT count(*) FROM "TaxiRoutes" rt WHERE rt."TaxiListingId" = l."Id" AND rt."HiddenByPlan")::int AS "HiddenRoutes",
               (SELECT count(*) FROM "TaxiRoutes" rt WHERE rt."TaxiListingId" = l."Id" AND NOT rt."HiddenByPlan")::int AS "VisibleRoutes",
               (SELECT count(*) FROM "ListingReports" x WHERE x."ListingId" = l."Id" AND x."ListingType" = 'Taxi' AND x."Status" = 'Pending')::int AS "ReportCount",
               (SELECT rr."Name" FROM "ListingReports" x JOIN "ReportReasons" rr ON rr."Id" = x."ReasonId"
                WHERE x."ListingId" = l."Id" AND x."ListingType" = 'Taxi' AND x."Status" = 'Pending' ORDER BY x."CreatedAt", x."Id" LIMIT 1) AS "FirstReportReason",
               {HasValidToken("l")} AS "HasValidToken",
               EXISTS (SELECT 1 FROM "UserNotices" n WHERE n."UserId" = @me AND n."Kind" = '{NoticeKinds.PlanLimitsChanged}' AND n."ReadAt" IS NULL) AS "PlansStartedNotice",
               (SELECT f."AnnounceDate" FROM "AppFeatureFlags" f WHERE f."FeatureKey" = '{AppFeatureKeys.PaymentEnabled}' LIMIT 1) AS "AnnounceDate",
               (SELECT f."PaymentEnabledAt" FROM "AppFeatureFlags" f WHERE f."FeatureKey" = '{AppFeatureKeys.PaymentEnabled}' LIMIT 1) AS "PaymentEnabledAt"
        FROM "TaxiListings" l
        JOIN "Users" own ON own."Id" = l."UserId"
        LEFT JOIN "TaxiVehicleTypes" vt ON vt."Id" = l."VehicleTypeId"
        LEFT JOIN "Districts" d ON d."Id" = l."DistrictId"
        LEFT JOIN "Cities" c ON c."Id" = l."CityId"
        LEFT JOIN LATERAL (
            SELECT u."Id", u."PlanId", u."Source", u."StartsAt", u."EndsAt", u."PlaySubscriptionId" FROM "UserPlans" u
            WHERE u."UserId" = @me AND u."FeatureKey" = '{ListingKinds.Taxi}' AND u."Status" = '{UserPlanStatuses.Active}' AND u."EndsAt" > now()
            ORDER BY u."EndsAt" DESC LIMIT 1) up ON true
        WHERE l."UserId" = @me AND NOT l."IsDeleted"
        LIMIT 1
        """;

    private static readonly string AcceptedOrLater = string.Join(", ", new[]
    {
        OfferStates.Accepted, OfferStates.Selected, OfferStates.NotSelected, OfferStates.NotAgreed, OfferStates.Confirmed, OfferStates.Withdrawn,
    }.Select(s => $"'{s}'"));

    // Statement 2. The AddRoutes example (AU-19) is computed only when he has a free route slot (@wantSuggestion).
    internal static readonly string WorkSql = $"""
        SELECT sel."Id" AS "SelectedOfferId", sel."ConfirmBy",
               (SELECT count(*) FROM "TaxiRequestOffers" o WHERE o."DriverListingId" = @listing
                  AND o."Status" IN ('{OfferStates.Ringing}', '{OfferStates.Selected}'))::int AS "LiveOffers",
               (SELECT count(*) FROM "TripEnquiries" e WHERE e."ListingId" = @listing AND e."Kind" = '{TripEnquiryKinds.Taxi}'
                  AND e."Status" = '{TripEnquiryStatuses.Asked}')::int AS "NewLeads",
               (SELECT count(*) FROM "TaxiRequestOffers" o WHERE o."DriverListingId" = @listing AND o."OfferedAt" >= now() - interval '7 days')::int AS "WeekRings",
               (SELECT count(*) FROM "TaxiRequestOffers" o WHERE o."DriverListingId" = @listing AND o."OfferedAt" >= now() - interval '7 days'
                  AND o."Status" IN ({AcceptedOrLater}) AND o."RespondedAt" IS NOT NULL)::int AS "WeekAccepted",
               (SELECT max(b."DateTo") FROM "TaxiBookings" b WHERE b."DriverListingId" = @listing AND b."Status" = '{BookingStates.InProgress}') AS "OnTripUntil",
               CASE WHEN @wantSuggestion THEN (
                   SELECT max(r."DropCityName") FILTER (WHERE r."DropCityKey" = r."MatchToCityKey")
                   FROM "TaxiRequests" r
                   WHERE r."CreatedAt" >= now() - interval '7 days'
                     AND r."MatchFromCityKey" IN (SELECT rt."FromCityKey" FROM "TaxiRoutes" rt WHERE rt."TaxiListingId" = @listing AND NOT rt."HiddenByPlan")
                     AND r."MatchToCityKey" NOT IN (SELECT rt."ToCityKey" FROM "TaxiRoutes" rt
                                                    WHERE rt."TaxiListingId" = @listing AND NOT rt."HiddenByPlan" AND rt."ToCityKey" IS NOT NULL)
                   GROUP BY r."MatchToCityKey"
                   ORDER BY count(*) DESC, r."MatchToCityKey"
                   LIMIT 1)
               END AS "SuggestedToCityName"
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT o."Id", o."ConfirmBy" FROM "TaxiRequestOffers" o
            WHERE o."DriverListingId" = @listing AND o."Status" = '{OfferStates.Selected}'
            ORDER BY o."ConfirmBy" NULLS LAST, o."Id" LIMIT 1) sel ON true
        """;

    public async Task<OwnerHomeDto?> ReadAsync(Guid userId, OwnerHomeEnv env, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var ctx = (await db.Database.SqlQueryRaw<TaxiContextRow>(ContextSql, Uuid("me", userId)).ToListAsync(ct)).FirstOrDefault();
        if (ctx is null) return null;
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var limits = OwnerContextReader.Build(userId, ListingKinds.Taxi, ctx.ToRow(), snapshot, flag, today).Limits;
        var approved = ctx.Status == TaxiStatuses.Approved;

        var work = (await db.Database.SqlQueryRaw<TaxiWorkRow>(WorkSql, Uuid("listing", ctx.ListingId),
            new Npgsql.NpgsqlParameter("wantSuggestion", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = approved && ctx.VisibleRoutes < limits.Routes })
            .ToListAsync(ct)).FirstOrDefault() ?? new TaxiWorkRow();
        var (upcoming, checkInsWeek) = await OwnerHomeShared.UpcomingAsync(db, ListingKinds.Taxi, ctx.ListingId, limits, today, ct);
        var (_, noRoute) = await OwnerHomeShared.WeeklyAsync(db, ListingKinds.Taxi, ctx.ListingId, today, ct);
        var festival = await OwnerHomeShared.FestivalAsync(db, ListingKinds.Taxi, ctx.ListingId, ctx.StateName, today, ct);

        var areaPaused = !ctx.DistrictActive;
        var hidden = new OwnerHiddenFacts(0, 0, ctx.HiddenRoutes, 0);
        var paymentStartsOn = OwnerHomeShared.PaymentStartsOn(flag.Enabled, ctx.AnnounceDate, today);
        var plansStartedToday = OwnerHomeShared.PlansStartedToday(flag.Enabled, ctx.PaymentEnabledAt, today);
        var ringReady = ctx.RingReadyAt != null && ctx.RingMissStreak < TaxiDispatchRules.RingMissLimit && ctx.HasValidToken;
        var newRequests = work.LiveOffers + work.NewLeads;

        OwnerPlanCardDto? planCard = null;
        var paymentIssue = false;
        if (approved && !areaPaused)
        {
            var extras = await OwnerHomeShared.PlanExtrasAsync(db, userId, ListingKinds.Taxi, ctx.PlaySubscriptionId, ct);
            paymentIssue = flag.Enabled && extras.PlayState == PlayStates.InGracePeriod;
            var weekly = new OwnerWeeklyDto(null, null, OwnerWeeklyRules.Shown(noRoute), OwnerWeeklyRules.Shown(work.WeekRings), OwnerWeeklyRules.Shown(work.WeekAccepted));
            planCard = OwnerHomeShared.PlanCard(limits, flag, snapshot, ListingKinds.Taxi, extras, hidden, weekly, paymentStartsOn, plansStartedToday, limits.RingWeight);
        }

        var attention = OwnerAttentionRules.Taxi(new TaxiAttentionFacts(
            work.SelectedOfferId, Utc(work.ConfirmBy), ctx.HasValidToken, ctx.RingMissStreak, paymentIssue, hidden, ctx.PlansStartedNotice,
            ctx.ReportCount, ctx.FirstReportReason, ctx.RingReadyAt != null, ctx.LocalDayRate != null, approved ? festival : null,
            ctx.VisibleRoutes, limits.Routes, ctx.CityName, work.SuggestedToCityName, approved));

        return new OwnerHomeDto
        {
            Role = UserRoles.TaxiDriver,
            Today = today,
            PaymentEnabled = flag.Enabled,
            Listing = new OwnerHomeListingDto
            {
                Id = ctx.ListingId,
                Kind = ListingKinds.Taxi,
                Status = ctx.Status,
                EverApproved = ctx.EverApproved,
                Name = ctx.DriverName,
                Sub = Sub(ctx.VehicleName, ctx.Seats),
                CityName = ctx.CityName,
                Plate = ctx.NumberPlate,
                SwitchOn = ctx.SwitchOn,
                IsFeatured = ctx.IsFeatured,
                TakenDown = ctx.Status == TaxiStatuses.Rejected && ctx.EverApproved,
                RejectedReason = ctx.RejectedReason,
                AreaPaused = areaPaused,
                TabOff = !env.TabActive,
                PlannerOff = !env.PlannerOn,
                PendingEdit = false,
                RingReady = ringReady,
                OnTripUntil = work.OnTripUntil,
                RoutesMeter = new OwnerMeterDto(ctx.VisibleRoutes, limits.Routes),
                StatusLine = ToDto(OwnerStatusLineRules.Choose(approved, ctx.SwitchOn, plansStartedToday, newRequests, 0,
                    limits.IsPaid, ctx.IsFeatured, ctx.CityName)),
            },
            Attention = attention.Select(a => new OwnerAttentionDto(a.Code, a.Priority, a.Args)).ToList(),
            AttentionCount = attention.Count,
            Numbers = new OwnerHomeNumbersDto
            {
                Taxi = new OwnerTaxiNumbersDto(newRequests, upcoming.Rows.FirstOrDefault(r => !r.Cancelled)?.DateFrom,
                    ctx.RatingCount > 0 ? Math.Round((double)ctx.RatingSum / ctx.RatingCount, 1) : null, ctx.RatingCount),
                CentreBadge = newRequests,
            },
            Upcoming = upcoming,
            PlanCard = planCard,
            Festival = approved ? OwnerHomeShared.ToDto(festival) : null,
        };
    }

    // "Maruti Ertiga · 6 seats" (12 = "12+").
    public static string? Sub(string? vehicle, short? seats)
    {
        var seatText = seats is { } s ? (s >= 12 ? "12+ seats" : $"{s} seats") : null;
        if (string.IsNullOrWhiteSpace(vehicle)) return seatText;
        return seatText is null ? vehicle : $"{vehicle} · {seatText}";
    }

    private static OwnerStatusLineDto? ToDto(OwnerStatusLine? line) => line is null ? null : new OwnerStatusLineDto(line.Code, line.Args);

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    public sealed class TaxiContextRow
    {
        public Guid ListingId { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? DistrictId { get; set; }
        public string? StateName { get; set; }
        public bool RemindersOn { get; set; }
        public DateOnly? SpecialPriceSavesDay { get; set; }
        public short SpecialPriceSavesCount { get; set; }
        public DateOnly? PlanVisibleSavesDay { get; set; }
        public short PlanVisibleSavesCount { get; set; }
        public Guid? UserPlanId { get; set; }
        public Guid? PlanId { get; set; }
        public string? Source { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public Guid? PlaySubscriptionId { get; set; }
        public string? DriverName { get; set; }
        public string? VehicleName { get; set; }
        public short? Seats { get; set; }
        public string? NumberPlate { get; set; }
        public string? CityName { get; set; }
        public bool SwitchOn { get; set; }
        public bool IsFeatured { get; set; }
        public bool EverApproved { get; set; }
        public string? RejectedReason { get; set; }
        public DateTime? RingReadyAt { get; set; }
        public short RingMissStreak { get; set; }
        public int? LocalDayRate { get; set; }
        public int RatingSum { get; set; }
        public int RatingCount { get; set; }
        public short RingWeight { get; set; }
        public bool DistrictActive { get; set; }
        public int HiddenRoutes { get; set; }
        public int VisibleRoutes { get; set; }
        public int ReportCount { get; set; }
        public string? FirstReportReason { get; set; }
        public bool HasValidToken { get; set; }
        public bool PlansStartedNotice { get; set; }
        public DateOnly? AnnounceDate { get; set; }
        public DateTime? PaymentEnabledAt { get; set; }

        public OwnerContextReader.Row ToRow() => new()
        {
            ListingId = ListingId, Status = Status, DistrictId = DistrictId, StateName = StateName, RemindersOn = RemindersOn,
            SpecialPriceSavesDay = SpecialPriceSavesDay, SpecialPriceSavesCount = SpecialPriceSavesCount,
            PlanVisibleSavesDay = PlanVisibleSavesDay, PlanVisibleSavesCount = PlanVisibleSavesCount,
            UserPlanId = UserPlanId, PlanId = PlanId, Source = Source, StartsAt = StartsAt, EndsAt = EndsAt,
        };
    }

    public sealed class TaxiWorkRow
    {
        public Guid? SelectedOfferId { get; set; }
        public DateTime? ConfirmBy { get; set; }
        public int LiveOffers { get; set; }
        public int NewLeads { get; set; }
        public int WeekRings { get; set; }
        public int WeekAccepted { get; set; }
        public DateOnly? OnTripUntil { get; set; }
        public string? SuggestedToCityName { get; set; }
    }
}

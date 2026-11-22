using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// GET /owner/home for a Stay Owner (contract 2.6 H1, AU-19, R-2, R-3). At most 6 statements, the same for 3 and 30 rows:
// (1) owner context with the listing facts, hidden counts, reports, token, notices and the payment dates; (2) the work numbers;
// (3) upcoming; (4) weekly; (5) festival; (6) plan card extras (skipped unless Approved in an active area).
public sealed class StayOwnerHomeReader(
    ApplicationDbContext db, IPlanCatalogCache catalog, IPaymentFeatureReader payment, TimeProvider clock, FestivalCache? festivals = null)
{
    internal static readonly string ContextSql = $"""
        SELECT l."Id" AS "ListingId", l."Status", l."DistrictId", d."StateName", l."RemindersOn",
               l."SpecialPriceSavesDay", l."SpecialPriceSavesCount", l."PlanVisibleSavesDay", l."PlanVisibleSavesCount",
               up."Id" AS "UserPlanId", up."PlanId", up."Source", up."StartsAt", up."EndsAt", up."PlaySubscriptionId",
               l."Name", c."Name" AS "CityName", l."AcceptingGuests" AS "SwitchOn", l."IsFeatured", l."EverApproved", l."RejectedReason",
               l."ReviewedAt", l."Slug", l."TripEnquiriesReceived",
               (COALESCE(l."UpiId", '') <> '' OR COALESCE(l."WhatsAppNumber", '') <> '') AS "HasShareDetails",
               (l."PendingEditJson" IS NOT NULL) AS "PendingEdit", l."PendingEditResult", l."PendingEditRejectReason", l."PendingEditResultAt",
               COALESCE(d."IsActive", false) AS "DistrictActive",
               (SELECT count(*) FROM "StayPhotos" p WHERE p."StayListingId" = l."Id" AND p."HiddenByPlan")::int AS "HiddenPhotos",
               (SELECT count(*) FROM "StayRooms" r WHERE r."StayListingId" = l."Id" AND r."HiddenByPlan")::int AS "HiddenRooms",
               (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = l."Id" AND sp."PausedByPlan" AND sp."DateTo" >= @today)::int AS "HiddenSpecial",
               (SELECT count(*) FROM "ListingReports" x WHERE x."ListingId" = l."Id" AND x."ListingType" = 'Stay' AND x."Status" = 'Pending')::int AS "ReportCount",
               (SELECT rr."Name" FROM "ListingReports" x JOIN "ReportReasons" rr ON rr."Id" = x."ReasonId"
                WHERE x."ListingId" = l."Id" AND x."ListingType" = 'Stay' AND x."Status" = 'Pending' ORDER BY x."CreatedAt", x."Id" LIMIT 1) AS "FirstReportReason",
               {HasValidToken("l")} AS "HasValidToken",
               EXISTS (SELECT 1 FROM "UserNotices" n WHERE n."UserId" = @me AND n."Kind" = '{NoticeKinds.PlanLimitsChanged}' AND n."ReadAt" IS NULL) AS "PlansStartedNotice",
               pf."AnnounceDate", pf."PaymentEnabledAt"
        FROM "StayListings" l
        LEFT JOIN "Districts" d ON d."Id" = l."DistrictId"
        LEFT JOIN "Cities" c ON c."Id" = l."CityId"
        LEFT JOIN LATERAL (
            SELECT u."Id", u."PlanId", u."Source", u."StartsAt", u."EndsAt", u."PlaySubscriptionId" FROM "UserPlans" u
            WHERE u."UserId" = @me AND u."FeatureKey" = '{ListingKinds.Stay}' AND u."Status" = '{UserPlanStatuses.Active}' AND u."EndsAt" > now()
            ORDER BY u."EndsAt" DESC LIMIT 1) up ON true
        LEFT JOIN LATERAL (
            SELECT f."AnnounceDate", f."PaymentEnabledAt" FROM "AppFeatureFlags" f WHERE f."FeatureKey" = '{AppFeatureKeys.PaymentEnabled}' LIMIT 1) pf ON true
        WHERE l."UserId" = @me AND NOT l."IsDeleted"
        LIMIT 1
        """;

    // Statement 2: one grouped read over his own open or recent stay enquiries (never the whole history) plus the newest change
    // request / counter and the newest guest accept. A change request is answerable only on a booking confirmed by both.
    internal static readonly string WorkSql = $"""
        SELECT a.*, chg."EnquiryId" AS "ChangeEnquiryId", chg."ChangeKind", chg."TravellerName" AS "ChangeTravellerName",
               acc."Id" AS "AcceptedEnquiryId", acc."TravellerName" AS "AcceptedTravellerName", acc."CheckIn" AS "AcceptedCheckIn", acc."CheckOut" AS "AcceptedCheckOut"
        FROM (
            SELECT count(*) FILTER (WHERE e."Status" = '{TripEnquiryStatuses.Asked}')::int AS "AskedCount",
                   count(*) FILTER (WHERE e."Status" = '{TripEnquiryStatuses.Countered}')::int AS "CounteredCount",
                   min(e."ExpiresAt") FILTER (WHERE e."Status" = '{TripEnquiryStatuses.Asked}') AS "SoonestReplyBy",
                   COALESCE(bool_or(e."IsUrgent") FILTER (WHERE e."Status" = '{TripEnquiryStatuses.Asked}'), false) AS "AnyUrgent",
                   count(*) FILTER (WHERE e."Status" = '{TripEnquiryStatuses.Offered}')::int AS "OffersWaiting",
                   count(*) FILTER (WHERE e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND e."CheckOut" >= @today)::int AS "Confirmed",
                   count(*) FILTER (WHERE e."CreatedAt" >= now() - interval '7 days')::int AS "WeekEnquiries",
                   (SELECT count(*) FROM "TripOffers" o JOIN "TripEnquiries" q ON q."Id" = o."TripEnquiryId"
                    WHERE q."OwnerUserId" = @me AND q."Kind" = '{TripEnquiryKinds.Stay}' AND q."ListingId" = @listing
                      AND q."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}'
                      AND o."Kind" = '{TripOfferKinds.ChangeRequest}' AND o."Status" = '{TripOfferStatuses.Pending}')::int AS "ChangeRequests"
            FROM "TripEnquiries" e
            WHERE e."OwnerUserId" = @me AND e."Kind" = '{TripEnquiryKinds.Stay}' AND e."ListingId" = @listing
              AND (e."Status" IN ('{TripEnquiryStatuses.Asked}', '{TripEnquiryStatuses.Countered}', '{TripEnquiryStatuses.Offered}', '{TripEnquiryStatuses.ConfirmedByBoth}')
                   OR e."CreatedAt" >= now() - interval '7 days')) a
        LEFT JOIN LATERAL (
            SELECT x."EnquiryId", x."ChangeKind", u."Name" AS "TravellerName" FROM (
                SELECT q."Id" AS "EnquiryId", '{ChangeOrCounterKinds.Counter}' AS "ChangeKind", q."TravellerUserId", q."UpdatedAt" AS "At"
                FROM "TripEnquiries" q
                WHERE q."OwnerUserId" = @me AND q."Kind" = '{TripEnquiryKinds.Stay}' AND q."ListingId" = @listing AND q."Status" = '{TripEnquiryStatuses.Countered}'
                UNION ALL
                SELECT q."Id", '{ChangeOrCounterKinds.Change}', q."TravellerUserId", o."CreatedAt"
                FROM "TripOffers" o JOIN "TripEnquiries" q ON q."Id" = o."TripEnquiryId"
                WHERE q."OwnerUserId" = @me AND q."Kind" = '{TripEnquiryKinds.Stay}' AND q."ListingId" = @listing
                  AND q."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}'
                  AND o."Kind" = '{TripOfferKinds.ChangeRequest}' AND o."Status" = '{TripOfferStatuses.Pending}') x
            LEFT JOIN "Users" u ON u."Id" = x."TravellerUserId"
            ORDER BY x."At" DESC, x."EnquiryId" DESC LIMIT 1) chg ON true
        LEFT JOIN LATERAL (
            SELECT q."Id", u."Name" AS "TravellerName", q."CheckIn", q."CheckOut"
            FROM "TripEnquiries" q LEFT JOIN "Users" u ON u."Id" = q."TravellerUserId"
            WHERE q."OwnerUserId" = @me AND q."Kind" = '{TripEnquiryKinds.Stay}' AND q."ListingId" = @listing
              AND q."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND q."ConfirmedByTravellerAt" >= now() - interval '24 hours'
            ORDER BY q."ConfirmedByTravellerAt" DESC, q."Id" DESC LIMIT 1) acc ON true
        """;

    public async Task<OwnerHomeDto?> ReadAsync(Guid userId, OwnerHomeEnv env, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var ctx = (await db.Database.SqlQueryRaw<StayContextRow>(ContextSql, Uuid("me", userId), Date("today", today)).ToListAsync(ct)).FirstOrDefault();
        if (ctx is null) return null;
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var limits = OwnerContextReader.Build(userId, ListingKinds.Stay, ctx.ToRow(), snapshot, flag, today).Limits;

        var work = (await db.Database.SqlQueryRaw<StayWorkRow>(WorkSql, Uuid("me", userId), Uuid("listing", ctx.ListingId), Date("today", today))
            .ToListAsync(ct)).FirstOrDefault() ?? new StayWorkRow();
        var (upcoming, checkInsWeek) = await OwnerHomeShared.UpcomingAsync(db, ListingKinds.Stay, ctx.ListingId, limits, today, ct);
        var (shown, _) = await OwnerHomeShared.WeeklyAsync(db, ListingKinds.Stay, ctx.ListingId, today, ct);
        var festival = await OwnerHomeShared.FestivalAsync(db, ListingKinds.Stay, ctx.ListingId, ctx.StateName, today, ct, festivals);

        var approved = ctx.Status == StayStatuses.Approved;
        var areaPaused = !ctx.DistrictActive;
        var hidden = new OwnerHiddenFacts(ctx.HiddenPhotos, ctx.HiddenRooms, 0, ctx.HiddenSpecial);
        var paymentStartsOn = OwnerHomeShared.PaymentStartsOn(flag.Enabled, ctx.AnnounceDate, today);
        var plansStartedToday = OwnerHomeShared.PlansStartedToday(flag.Enabled, ctx.PaymentEnabledAt, today);

        OwnerPlanCardDto? planCard = null;
        var paymentIssue = false;
        // FB-34: no plan card while the Stays tab is off.
        if (approved && !areaPaused && env.TabActive)
        {
            var extras = await OwnerHomeShared.PlanExtrasAsync(db, userId, ListingKinds.Stay, ctx.PlaySubscriptionId, ct);
            paymentIssue = flag.Enabled && extras.PlayState == PlayStates.InGracePeriod;
            var weekly = new OwnerWeeklyDto(OwnerWeeklyRules.Shown(shown), OwnerWeeklyRules.Shown(work.WeekEnquiries), null, null, null);
            planCard = OwnerHomeShared.PlanCard(limits, flag, snapshot, ListingKinds.Stay, extras, hidden, weekly, paymentStartsOn, plansStartedToday, null);
        }

        var editInWindow = StayEditRejectedInWindow(ctx.PendingEdit, ctx.PendingEditResult, ctx.PendingEditResultAt, now);
        var attention = OwnerAttentionRules.Stay(new StayAttentionFacts(
            work.AskedCount, Utc(work.SoonestReplyBy), work.AnyUrgent,
            work.ChangeEnquiryId, work.ChangeKind, work.ChangeTravellerName,
            work.AcceptedEnquiryId, work.AcceptedTravellerName, work.AcceptedCheckIn, work.AcceptedCheckOut,
            paymentIssue, hidden, ctx.PlansStartedNotice, ctx.ReportCount, ctx.FirstReportReason,
            ctx.PendingEditRejectReason, [], editInWindow, approved ? festival : null,
            approved, Utc(ctx.ReviewedAt), ctx.HasShareDetails, ctx.TripEnquiriesReceived, ctx.Slug), now);

        return new OwnerHomeDto
        {
            Role = UserRoles.StayOwner,
            Today = today,
            PaymentEnabled = flag.Enabled,
            Listing = new OwnerHomeListingDto
            {
                Id = ctx.ListingId,
                Kind = ListingKinds.Stay,
                Status = ctx.Status,
                EverApproved = ctx.EverApproved,
                Name = ctx.Name,
                Sub = ctx.CityName,
                CityName = ctx.CityName,
                SwitchOn = ctx.SwitchOn,
                IsFeatured = ctx.IsFeatured,
                TakenDown = ctx.Status == StayStatuses.Rejected && ctx.EverApproved,
                RejectedReason = ctx.RejectedReason,
                AreaPaused = areaPaused,
                TabOff = !env.TabActive,
                PlannerOff = !env.PlannerOn,
                PendingEdit = ctx.PendingEdit,
                StatusLine = ToDto(OwnerStatusLineRules.Choose(approved, ctx.SwitchOn, plansStartedToday, work.AskedCount, checkInsWeek,
                    ctx.IsFeatured, ctx.CityName)),
            },
            Attention = attention.Select(a => new OwnerAttentionDto(a.Code, a.Priority, a.Args)).ToList(),
            AttentionCount = attention.Count,
            Numbers = new OwnerHomeNumbersDto
            {
                Stay = new OwnerStayNumbersDto(work.AskedCount + work.CounteredCount, work.OffersWaiting, work.Confirmed),
                // FB-17: Asked, Countered and change requests all wait for the host.
                CentreBadge = work.AskedCount + work.CounteredCount + work.ChangeRequests,
            },
            Upcoming = upcoming,
            PlanCard = planCard,
            Festival = approved ? OwnerHomeShared.ToDto(festival) : null,
        };
    }

    // The 7-day strip of a rejected change (AU-5): shown while no newer change waits.
    public static bool StayEditRejectedInWindow(bool pendingEdit, string? result, DateTime? resultAt, DateTime nowUtc)
        => !pendingEdit && result == PendingEditResults.Rejected && resultAt is { } at
           && DateTime.SpecifyKind(at, DateTimeKind.Utc) >= nowUtc.AddDays(-OwnerLimits.EditResultStripDays);

    private static OwnerStatusLineDto? ToDto(OwnerStatusLine? line) => line is null ? null : new OwnerStatusLineDto(line.Code, line.Args);

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    public sealed class StayContextRow
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

        public OwnerContextReader.Row ToRow() => new()
        {
            ListingId = ListingId, Status = Status, DistrictId = DistrictId, StateName = StateName, RemindersOn = RemindersOn,
            SpecialPriceSavesDay = SpecialPriceSavesDay, SpecialPriceSavesCount = SpecialPriceSavesCount,
            PlanVisibleSavesDay = PlanVisibleSavesDay, PlanVisibleSavesCount = PlanVisibleSavesCount,
            UserPlanId = UserPlanId, PlanId = PlanId, Source = Source, StartsAt = StartsAt, EndsAt = EndsAt,
        };

        public Guid? PlaySubscriptionId { get; set; }
        public string? Name { get; set; }
        public string? CityName { get; set; }
        public bool SwitchOn { get; set; }
        public bool IsFeatured { get; set; }
        public bool EverApproved { get; set; }
        public string? RejectedReason { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? Slug { get; set; }
        public int TripEnquiriesReceived { get; set; }
        public bool HasShareDetails { get; set; }
        public bool PendingEdit { get; set; }
        public string? PendingEditResult { get; set; }
        public string? PendingEditRejectReason { get; set; }
        public DateTime? PendingEditResultAt { get; set; }
        public bool DistrictActive { get; set; }
        public int HiddenPhotos { get; set; }
        public int HiddenRooms { get; set; }
        public int HiddenSpecial { get; set; }
        public int ReportCount { get; set; }
        public string? FirstReportReason { get; set; }
        public bool HasValidToken { get; set; }
        public bool PlansStartedNotice { get; set; }
        public DateOnly? AnnounceDate { get; set; }
        public DateTime? PaymentEnabledAt { get; set; }
    }

    public sealed class StayWorkRow
    {
        public int AskedCount { get; set; }
        public int CounteredCount { get; set; }
        public DateTime? SoonestReplyBy { get; set; }
        public bool AnyUrgent { get; set; }
        public int OffersWaiting { get; set; }
        public int Confirmed { get; set; }
        public int WeekEnquiries { get; set; }
        public int ChangeRequests { get; set; }
        public Guid? ChangeEnquiryId { get; set; }
        public string? ChangeKind { get; set; }
        public string? ChangeTravellerName { get; set; }
        public Guid? AcceptedEnquiryId { get; set; }
        public string? AcceptedTravellerName { get; set; }
        public DateOnly? AcceptedCheckIn { get; set; }
        public DateOnly? AcceptedCheckOut { get; set; }
    }
}

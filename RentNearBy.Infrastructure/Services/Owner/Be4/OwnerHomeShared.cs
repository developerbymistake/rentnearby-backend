using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Outside facts of the Home read that come from caches, not from the database (tab snapshot, planner switch).
public sealed record OwnerHomeEnv(bool TabActive, bool PlannerOn);

// Statements 3 to 6 of the Home read (contract 2.6 H1), kind-agnostic: upcoming rows, weekly numbers, festival row, plan card extras.
public static class OwnerHomeShared
{
    public const int UpcomingTake = 3;

    // Statement 3: the first 3 visible rows of the Upcoming window (Auto always; Manual only among the plan's first active rows by
    // (DateTo, Id), contract 2.1 visibility rule), the window count, Auto rows past the window and check-ins of the next 7 days.
    internal const string UpcomingSql = """
        WITH vis AS (
            SELECT v."Id" FROM "CalendarEntries" v
            WHERE v."ListingKind" = @kind AND v."ListingId" = @listing AND v."Kind" = 'Manual' AND v."DateTo" >= @today
            ORDER BY v."DateTo", v."Id" LIMIT @manualLimit),
        w AS (
            SELECT e."Id", e."Kind", e."DateFrom", e."DateTo", e."TimeOfDay", e."Label", e."Detail", e."FromPlace", e."ToPlace",
                   e."FinalAmount", e."AdvanceReceived", e."PaidInFull", e."Cancelled", e."TravellerUserId"
            FROM "CalendarEntries" e
            WHERE e."ListingKind" = @kind AND e."ListingId" = @listing AND e."DateTo" >= @today AND e."DateFrom" <= @upTo
              AND (e."Kind" = 'Auto' OR e."Id" IN (SELECT vis."Id" FROM vis))),
        s AS (
            SELECT (SELECT count(*) FROM w)::int AS "InWindow",
                   (SELECT count(*) FROM w WHERE NOT w."Cancelled" AND w."DateFrom" BETWEEN @today AND @weekTo)::int AS "CheckInsWeek",
                   (SELECT count(*) FROM "CalendarEntries" b
                    WHERE b."ListingKind" = @kind AND b."ListingId" = @listing AND b."Kind" = 'Auto' AND NOT b."Cancelled" AND b."DateFrom" > @upTo)::int AS "BeyondCount",
                   (SELECT min(b."DateFrom") FROM "CalendarEntries" b
                    WHERE b."ListingKind" = @kind AND b."ListingId" = @listing AND b."Kind" = 'Auto' AND NOT b."Cancelled" AND b."DateFrom" > @upTo) AS "BeyondFirst")
        SELECT s."InWindow", s."CheckInsWeek", s."BeyondCount", s."BeyondFirst",
               x."Id", x."Kind", x."DateFrom", x."DateTo", x."TimeOfDay", x."Label", x."Detail", x."FromPlace", x."ToPlace",
               x."FinalAmount", x."AdvanceReceived", x."PaidInFull", x."Cancelled", x."TravellerUserId"
        FROM s
        LEFT JOIN LATERAL (SELECT * FROM w ORDER BY w."DateFrom", w."Id" LIMIT 3) x ON true
        ORDER BY x."DateFrom", x."Id"
        """;

    // Statement 4: the stored weekly numbers of the 7 full IST days before today.
    internal const string WeeklySql = """
        SELECT count(*)::int AS "Days", COALESCE(sum("Shown"), 0)::bigint AS "Shown", COALESCE(sum("RequestsNoRoute"), 0)::bigint AS "NoRoute"
        FROM "OwnerWeeklyStats" WHERE "ListingKind" = @kind AND "ListingId" = @listing AND "Day" BETWEEN @from AND @to
        """;

    // Statement 5: the next festival of his state (or all India) inside the lead window; a stay with a special price touching its dates
    // has nothing to do, so it gets no row.
    internal static string FestivalSql(string kind) => $"""
        SELECT f."Id", f."Name", f."DateFrom", f."DateTo", f."StateName"
        FROM "FestivalDates" f
        WHERE f."IsActive" AND f."DateFrom" >= @today AND f."DateFrom" <= @leadTo
          AND (f."StateName" IS NULL OR f."StateName" = @state::text)
          {(kind == ListingKinds.Stay ? """AND NOT EXISTS (SELECT 1 FROM "StaySpecialPrices" r WHERE r."StayListingId" = @listing AND r."DateFrom" <= f."DateTo" AND r."DateTo" >= f."DateFrom")""" : string.Empty)}
        ORDER BY f."DateFrom", f."Id"
        LIMIT 1
        """;

    // Statement 6: plan card extras (the /plans/me rule: the active row's subscription first, else the latest Mapped one).
    internal static readonly string PlanExtrasSql = $"""
        SELECT ps."State" AS "PlayState", COALESCE(ps."AutoRenewEnabled", false) AS "PlayAutoRenew", ps."ExpiryTime" AS "PlayExpiry",
               EXISTS (SELECT 1 FROM "PlaySubscriptions" x WHERE x."UserId" = @me AND x."FeatureKey" = @kind) AS "AnyPlay",
               EXISTS (SELECT 1 FROM "UserPlans" u WHERE u."UserId" = @me AND u."FeatureKey" = @kind) AS "AnyPlan",
               EXISTS (SELECT 1 FROM "UserPlans" u WHERE u."UserId" = @me AND u."FeatureKey" = @kind AND u."Source" = '{UserPlanSources.Trial}') AS "TrialUsed",
               -- FB-39: a non-Play row past its end that the expiry pass has not claimed yet already reads as ended.
               EXISTS (SELECT 1 FROM "UserPlans" u WHERE u."UserId" = @me AND u."FeatureKey" = @kind
                         AND u."Source" <> '{UserPlanSources.AdminGrant}' AND u."EndsAt" >= now() - interval '30 days'
                         AND (u."Status" <> '{UserPlanStatuses.Active}' OR (u."PlaySubscriptionId" IS NULL AND u."EndsAt" <= now()))) AS "EndedRecently",
               -- FB-12: a Google Play trial offer still running on the active row.
               (SELECT u."PlayTrialEndsAt" FROM "UserPlans" u WHERE u."UserId" = @me AND u."FeatureKey" = @kind
                  AND u."Status" = '{UserPlanStatuses.Active}' ORDER BY u."EndsAt" DESC LIMIT 1) AS "PlayTrialEndsAt"
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT s."State", s."AutoRenewEnabled", s."ExpiryTime" FROM "PlaySubscriptions" s
            WHERE s."UserId" = @me AND s."FeatureKey" = @kind AND s."MappingStatus" = '{PlayMappingStatuses.Mapped}'
            ORDER BY (s."Id" = @activeSub) DESC, s."UpdatedAt" DESC
            LIMIT 1) ps ON true
        """;

    public static async Task<(OwnerUpcomingDto Upcoming, int CheckInsWeek)> UpcomingAsync(
        ApplicationDbContext db, string kind, Guid listingId, PlanLimits limits, DateOnly today, CancellationToken ct)
    {
        var window = OwnerCalendarWindow.Of(limits, today);
        var rows = await db.Database.SqlQueryRaw<UpcomingRow>(UpcomingSql,
            Text("kind", kind), Uuid("listing", listingId), Date("today", today), Date("upTo", window.UpcomingTo),
            Date("weekTo", today.AddDays(6)), Int("manualLimit", Math.Max(0, limits.CalendarEntries))).ToListAsync(ct);
        return BuildUpcoming(rows, limits, window);
    }

    public static (OwnerUpcomingDto Upcoming, int CheckInsWeek) BuildUpcoming(IReadOnlyList<UpcomingRow> rows, PlanLimits limits, OwnerCalendarWindowValue window)
    {
        var head = rows.FirstOrDefault();
        var items = rows.Where(r => r.Id is not null).Take(UpcomingTake).Select(ToRow).ToList();
        var inWindow = head?.InWindow ?? 0;
        return (new OwnerUpcomingDto
        {
            Rows = items,
            MoreInWindowCount = Math.Max(0, inWindow - items.Count),
            WindowLabelDays = limits.UpcomingDays,
            BeyondWindowAppCount = head?.BeyondCount ?? 0,
            BeyondWindowFirstDate = head?.BeyondFirst,
            Window = WindowDto.From(window),
        }, head?.CheckInsWeek ?? 0);
    }

    public static OwnerUpcomingRowDto ToRow(UpcomingRow r)
    {
        var view = MoneyNoteMath.View(new MoneyNote(r.FinalAmount, r.AdvanceReceived, r.PaidInFull ?? false));
        var isAuto = r.Kind == CalendarEntryKinds.Auto;
        return new OwnerUpcomingRowDto
        {
            Id = r.Id!.Value,
            Kind = r.Kind ?? CalendarEntryKinds.Manual,
            DateFrom = r.DateFrom ?? default,
            DateTo = r.DateTo ?? default,
            TimeOfDay = r.TimeOfDay,
            Label = r.Label ?? string.Empty,
            Detail = r.Detail,
            FromPlace = r.FromPlace,
            ToPlace = r.ToPlace,
            Chip = view.Chip,
            ChipAmount = view.ChipAmount,
            Cancelled = r.Cancelled ?? false,
            TravellerRemoved = isAuto && r.TravellerUserId is null,
        };
    }

    public static async Task<(long? Shown, long? NoRoute)> WeeklyAsync(ApplicationDbContext db, string kind, Guid listingId, DateOnly today, CancellationToken ct)
    {
        var row = (await db.Database.SqlQueryRaw<WeeklyRow>(WeeklySql,
            Text("kind", kind), Uuid("listing", listingId), Date("from", OwnerWeeklyRules.From(today)), Date("to", OwnerWeeklyRules.To(today)))
            .ToListAsync(ct)).FirstOrDefault();
        return row is null || row.Days == 0 ? (null, null) : (row.Shown, row.NoRoute);
    }

    // Statement 5, cache first (contract 2.6 point 5): the candidates come from the F1 cache; a taxi needs no statement, a stay runs
    // one only when a candidate exists (the first candidate without a special price touching its dates). No cache = the SQL above.
    internal const string FirstFreeFestivalSql = """
        SELECT f.id AS "Value"
        FROM unnest(@ids, @froms, @tos) WITH ORDINALITY AS f(id, dfrom, dto, ord)
        WHERE NOT EXISTS (SELECT 1 FROM "StaySpecialPrices" r WHERE r."StayListingId" = @listing AND r."DateFrom" <= f.dto AND r."DateTo" >= f.dfrom)
        ORDER BY f.ord
        LIMIT 1
        """;

    public static IReadOnlyList<FestivalCache.Row> Candidates(IReadOnlyList<FestivalCache.Row> rows, string kind, DateOnly today)
    {
        var leadTo = today.AddDays(kind == ListingKinds.Stay ? OwnerLimits.FestivalStayLeadDays : OwnerLimits.FestivalTaxiLeadDays);
        return rows.Where(f => f.DateFrom >= today && f.DateFrom <= leadTo).OrderBy(f => f.DateFrom).ThenBy(f => f.Id).ToList();
    }

    public static async Task<OwnerFestivalFacts?> FestivalAsync(
        ApplicationDbContext db, string kind, Guid listingId, string? stateName, DateOnly today, CancellationToken ct, FestivalCache? festivals = null)
    {
        if (festivals != null)
        {
            var candidates = Candidates(await festivals.GetRowsAsync(db, stateName, today, ct), kind, today);
            if (candidates.Count == 0) return null;
            var chosen = candidates[0];
            if (kind == ListingKinds.Stay)
            {
                var free = await db.Database.SqlQueryRaw<Guid>(FirstFreeFestivalSql,
                    new Npgsql.NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = candidates.Select(f => f.Id).ToArray() },
                    new Npgsql.NpgsqlParameter("froms", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Date) { Value = candidates.Select(f => f.DateFrom).ToArray() },
                    new Npgsql.NpgsqlParameter("tos", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Date) { Value = candidates.Select(f => f.DateTo).ToArray() },
                    Uuid("listing", listingId)).ToListAsync(ct);
                if (free.Count == 0) return null;
                chosen = candidates.First(f => f.Id == free[0]);
            }
            return new OwnerFestivalFacts(chosen.Id, chosen.Name, chosen.DateFrom, chosen.DateTo, chosen.StateName, chosen.DateFrom.DayNumber - today.DayNumber);
        }

        var lead = kind == ListingKinds.Stay ? OwnerLimits.FestivalStayLeadDays : OwnerLimits.FestivalTaxiLeadDays;
        var row = (await db.Database.SqlQueryRaw<FestivalRow>(FestivalSql(kind),
            Date("today", today), Date("leadTo", today.AddDays(lead)), NullableText("state", stateName), Uuid("listing", listingId))
            .ToListAsync(ct)).FirstOrDefault();
        return row is null ? null : new OwnerFestivalFacts(row.Id, row.Name, row.DateFrom, row.DateTo, row.StateName, row.DateFrom.DayNumber - today.DayNumber);
    }

    public static async Task<PlanExtrasRow> PlanExtrasAsync(ApplicationDbContext db, Guid userId, string kind, Guid? activeSubscriptionId, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<PlanExtrasRow>(PlanExtrasSql,
            Uuid("me", userId), Text("kind", kind), Uuid("activeSub", activeSubscriptionId ?? Guid.Empty)).ToListAsync(ct)).FirstOrDefault() ?? new PlanExtrasRow();

    public static OwnerFestivalDto? ToDto(OwnerFestivalFacts? f)
        => f is null ? null : new OwnerFestivalDto(f.Id, f.Name, f.DateFrom, f.DateTo, f.StateName, f.DaysAway);

    // The /plans/me trial rule: a trial is offered (Play: a trial offer on a 1-month Play price; Razorpay: an enabled plan with trial
    // days) and none was used for the feature.
    public static bool TrialAvailable(PlanCatalogSnapshot snapshot, PaymentFeatureState flag, string kind, PlanExtrasRow extras)
    {
        if (!flag.Enabled) return false;
        if (flag.Provider == PaymentProviders.Play)
        {
            var offered = snapshot.PlansOf(kind).Any(p => p.IsEnabled && !p.IsDefault && p.PlayProductId != null
                && p.Prices.Any(x => x.IsEnabled && x.CycleMonths == 1 && x.PlayBasePlanId != null && x.PlayTrialOfferId != null));
            return offered && !extras.AnyPlay && !extras.TrialUsed;
        }
        return snapshot.PlansOf(kind).Any(p => p.IsEnabled && !p.IsDefault && p.TrialDays > 0) && !extras.AnyPlan;
    }

    public static OwnerPlanCardDto PlanCard(PlanLimits limits, PaymentFeatureState flag, PlanCatalogSnapshot snapshot, string kind, PlanExtrasRow extras,
        OwnerHiddenFacts hidden, OwnerWeeklyDto weekly, DateOnly? paymentStartsOn, bool plansStartedToday, int? ringWeight)
    {
        var inPlayTrial = extras.PlayTrialEndsAt is { } trialEnd && trialEnd > DateTime.UtcNow;
        var state = PlanCardState(limits, flag, extras);
        return new()
        {
            State = state,
            PaymentEnabled = flag.Enabled,
            IsPaid = limits.IsPaid,
            Source = limits.Source,
            EndsAt = state == OwnerPlanCardStates.Trial && inPlayTrial ? extras.PlayTrialEndsAt : limits.EndsAt,
            RenewsOn = extras.PlayAutoRenew && extras.PlayState == PlayStates.Active ? extras.PlayExpiry : null,
            TrialAvailable = TrialAvailable(snapshot, flag, kind, extras),
            Limits = LimitsDto(limits),
            Hidden = new OwnerHiddenDto(hidden.Photos, hidden.RoomTypes, hidden.Routes, hidden.SpecialPrices),
            RingWeight = ringWeight,
            PaymentStartsOn = paymentStartsOn,
            PlansStartedToday = plansStartedToday,
            Weekly = weekly,
        };
    }

    // The one state rule of Home and /plans/me (FB-12, FB-50).
    public static string PlanCardState(PlanLimits limits, PaymentFeatureState flag, PlanExtrasRow extras)
        => OwnerPlanCardStates.Of(flag.Enabled, limits.UserPlanId != null ? limits.Source : null, extras.PlayState, extras.PlayAutoRenew,
            extras.EndedRecently, extras.PlayTrialEndsAt is { } trialEnd && trialEnd > DateTime.UtcNow);

    public static MyPlanLimitsDto LimitsDto(PlanLimits l) => new()
    {
        ListingLimit = l.ListingLimit,
        Priority = l.PriorityRank,
        Featured = l.IsFeatured,
        JobsPosts = l.JobsPosts,
        Photos = l.Photos,
        RoomTypes = l.RoomTypes,
        Routes = l.Routes,
        RingWeight = l.RingWeight,
        CalendarEntries = l.CalendarEntries,
        BlockedRanges = l.BlockedRanges,
        UpcomingDays = l.UpcomingDays,
        MonthsAhead = l.MonthsAhead,
        MonthSummary = l.MonthSummary,
        Reminders = l.Reminders,
        SpecialPrices = l.SpecialPrices,
        DemandCount = l.DemandCount,
    };

    // AU-18: the announce date while payment is OFF and the date is ahead; "plans started today" = switched on this IST day.
    public static DateOnly? PaymentStartsOn(bool paymentEnabled, DateOnly? announceDate, DateOnly today)
        => !paymentEnabled && announceDate is { } d && d > today ? d : null;

    public static bool PlansStartedToday(bool paymentEnabled, DateTime? paymentEnabledAt, DateOnly today)
        => paymentEnabled && paymentEnabledAt is { } at && IstTime.TodayIst(DateTime.SpecifyKind(at, DateTimeKind.Utc)) == today;

    public static int? Small(long value) => (int)Math.Min(value, int.MaxValue);

    public sealed class UpcomingRow
    {
        public int InWindow { get; set; }
        public int CheckInsWeek { get; set; }
        public int BeyondCount { get; set; }
        public DateOnly? BeyondFirst { get; set; }
        public Guid? Id { get; set; }
        public string? Kind { get; set; }
        public DateOnly? DateFrom { get; set; }
        public DateOnly? DateTo { get; set; }
        public TimeOnly? TimeOfDay { get; set; }
        public string? Label { get; set; }
        public string? Detail { get; set; }
        public string? FromPlace { get; set; }
        public string? ToPlace { get; set; }
        public int? FinalAmount { get; set; }
        public int? AdvanceReceived { get; set; }
        public bool? PaidInFull { get; set; }
        public bool? Cancelled { get; set; }
        public Guid? TravellerUserId { get; set; }
    }

    private sealed class WeeklyRow
    {
        public int Days { get; set; }
        public long Shown { get; set; }
        public long NoRoute { get; set; }
    }

    private sealed class FestivalRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public string? StateName { get; set; }
    }

    public sealed class PlanExtrasRow
    {
        public string? PlayState { get; set; }
        public bool PlayAutoRenew { get; set; }
        public DateTime? PlayExpiry { get; set; }
        public bool AnyPlay { get; set; }
        public bool AnyPlan { get; set; }
        public bool TrialUsed { get; set; }
        public bool EndedRecently { get; set; }
        public DateTime? PlayTrialEndsAt { get; set; }
    }
}

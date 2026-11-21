using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// Contract 2.6 H1, AU-19, R-2, R-3: attention rows, the status line, weekly numbers and the plan card state (pure parts).
public class OwnerAttentionRulesTests
{
    private static readonly DateTime Now = Be4Facts.Now;

    private static StayAttentionFacts Stay(
        int newCount = 0, Guid? change = null, Guid? accepted = null, bool paymentIssue = false, OwnerHiddenFacts? hidden = null,
        bool plansStarted = false, int reports = 0, string? editReason = null, bool editInWindow = false, OwnerFestivalFacts? festival = null,
        bool approved = true, DateTime? approvedAt = null, bool shareDetails = true, int everReceived = 1, string? slug = "lake-view")
        => new(newCount, Now.AddHours(3), true, change, ChangeOrCounterKinds.Counter, "Rohit", accepted, "Rohit",
            new DateOnly(2026, 11, 27), new DateOnly(2026, 11, 29), paymentIssue, hidden ?? OwnerHiddenFacts.None, plansStarted,
            reports, "Wrong photos", editReason, ["name"], editInWindow, festival, approved, approvedAt, shareDetails, everReceived, slug);

    private static readonly OwnerFestivalFacts Diwali = new(Guid.NewGuid(), "Diwali", new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 14), null, 19);

    [Fact]
    public void Every_stay_row_true_comes_in_doc_order_with_the_R3_swap()
    {
        var rows = OwnerAttentionRules.Stay(Stay(newCount: 2, change: Guid.NewGuid(), accepted: Guid.NewGuid(), paymentIssue: true,
            hidden: new OwnerHiddenFacts(4, 2, 0, 0), reports: 1, editReason: "Blurry", editInWindow: true, festival: Diwali,
            approvedAt: Now.AddDays(-2), shareDetails: false, everReceived: 0), Now);

        Assert.Equal(
        [
            OwnerAttentionCodes.EnquiriesWaiting, OwnerAttentionCodes.ChangeOrCounter, OwnerAttentionCodes.OfferAccepted,
            OwnerAttentionCodes.PaymentIssue, OwnerAttentionCodes.PlanHidden, OwnerAttentionCodes.Reports, OwnerAttentionCodes.EditRejected,
            OwnerAttentionCodes.Festival, OwnerAttentionCodes.ShareListing, OwnerAttentionCodes.AddShareDetails,
        ], rows.Select(r => r.Code));
        Assert.Equal([1, 2, 3, 4, 6, 7, 8, 9, 10, 11], rows.Select(r => r.Priority));
        Assert.DoesNotContain(rows, r => r.Code == OwnerAttentionCodes.NotificationsOff);
    }

    [Fact]
    public void A_fresh_stay_owner_has_no_row()
        => Assert.Empty(OwnerAttentionRules.Stay(Stay(), Now));

    [Fact]
    public void Enquiries_waiting_carries_count_reply_by_and_urgent()
    {
        var row = Assert.Single(OwnerAttentionRules.Stay(Stay(newCount: 2), Now));
        Assert.Equal(2, row.Args["count"]);
        Assert.Equal(Now.AddHours(3), row.Args["replyBy"]);
        Assert.Equal(true, row.Args["urgent"]);
    }

    [Theory]
    [InlineData(true, PlanHiddenReasons.PlansStarted)]
    [InlineData(false, PlanHiddenReasons.PlusEnded)]
    public void Plan_hidden_reason_follows_the_unread_notice(bool notice, string reason)
    {
        var row = Assert.Single(OwnerAttentionRules.Stay(Stay(hidden: new OwnerHiddenFacts(4, 2, 0, 1), plansStarted: notice), Now));
        Assert.Equal(OwnerAttentionCodes.PlanHidden, row.Code);
        Assert.Equal(reason, row.Args["reason"]);
        Assert.Equal(4, row.Args["photos"]);
        Assert.Equal(2, row.Args["roomTypes"]);
    }

    [Fact]
    public void Hidden_special_prices_alone_raise_no_plan_hidden_row()
        => Assert.Empty(OwnerAttentionRules.Stay(Stay(hidden: new OwnerHiddenFacts(0, 0, 0, 2)), Now));

    [Fact]
    public void Add_share_details_only_in_the_first_7_days_after_approval()
    {
        Assert.Contains(OwnerAttentionRules.Stay(Stay(shareDetails: false, approvedAt: Now.AddDays(-7)), Now), r => r.Code == OwnerAttentionCodes.AddShareDetails);
        Assert.DoesNotContain(OwnerAttentionRules.Stay(Stay(shareDetails: false, approvedAt: Now.AddDays(-8)), Now), r => r.Code == OwnerAttentionCodes.AddShareDetails);
        Assert.DoesNotContain(OwnerAttentionRules.Stay(Stay(shareDetails: true, approvedAt: Now.AddDays(-1)), Now), r => r.Code == OwnerAttentionCodes.AddShareDetails);
    }

    [Fact]
    public void Share_listing_until_the_first_enquiry_and_only_when_approved()
    {
        Assert.Contains(OwnerAttentionRules.Stay(Stay(everReceived: 0), Now), r => r.Code == OwnerAttentionCodes.ShareListing);
        Assert.DoesNotContain(OwnerAttentionRules.Stay(Stay(everReceived: 1), Now), r => r.Code == OwnerAttentionCodes.ShareListing);
        Assert.DoesNotContain(OwnerAttentionRules.Stay(Stay(everReceived: 0, approved: false), Now), r => r.Code == OwnerAttentionCodes.ShareListing);
    }

    [Fact]
    public void Edit_rejected_only_inside_its_window()
    {
        Assert.Contains(OwnerAttentionRules.Stay(Stay(editReason: "Blurry", editInWindow: true), Now), r => r.Code == OwnerAttentionCodes.EditRejected);
        Assert.Empty(OwnerAttentionRules.Stay(Stay(editReason: "Blurry", editInWindow: false), Now));
    }

    private static TaxiAttentionFacts Taxi(
        Guid? selected = null, bool token = true, int missed = 0, bool paymentIssue = false, int hiddenRoutes = 0, int reports = 0,
        bool ringReady = true, bool localRate = true, OwnerFestivalFacts? festival = null, int visibleRoutes = 1, int routesLimit = 1,
        bool approved = true)
        => new(selected, Now.AddMinutes(12), token, missed, paymentIssue, new OwnerHiddenFacts(0, 0, hiddenRoutes, 0), false, reports, "Rude",
            ringReady, localRate, festival, visibleRoutes, routesLimit, "Haldwani", "Badrinath", approved);

    [Fact]
    public void Every_taxi_row_true_comes_in_doc_order()
    {
        var rows = OwnerAttentionRules.Taxi(Taxi(selected: Guid.NewGuid(), token: false, missed: 3, paymentIssue: true, hiddenRoutes: 3,
            reports: 1, ringReady: false, localRate: false, festival: Diwali, visibleRoutes: 1, routesLimit: 5));
        Assert.Equal(
        [
            OwnerAttentionCodes.TravellerChoseYou, OwnerAttentionCodes.NoPushToken, OwnerAttentionCodes.MissedRings, OwnerAttentionCodes.PaymentIssue,
            OwnerAttentionCodes.PlanHidden, OwnerAttentionCodes.Reports, OwnerAttentionCodes.AddLocalRate, OwnerAttentionCodes.Festival,
            OwnerAttentionCodes.AddRoutes,
        ], rows.Select(r => r.Code));
        Assert.Equal([1, 2, 4, 5, 6, 7, 9, 10, 11], rows.Select(r => r.Priority));
    }

    [Fact]
    public void Get_ready_until_the_first_good_test_ring()
    {
        Assert.Contains(OwnerAttentionRules.Taxi(Taxi(ringReady: false)), r => r.Code == OwnerAttentionCodes.GetReady);
        Assert.DoesNotContain(OwnerAttentionRules.Taxi(Taxi(ringReady: true)), r => r.Code == OwnerAttentionCodes.GetReady);
    }

    [Fact]
    public void Add_routes_only_with_a_free_route_slot()
    {
        var row = Assert.Single(OwnerAttentionRules.Taxi(Taxi(visibleRoutes: 2, routesLimit: 5)));
        Assert.Equal(OwnerAttentionCodes.AddRoutes, row.Code);
        Assert.Equal("Haldwani", row.Args["fromCityName"]);
        Assert.Equal("Badrinath", row.Args["toCityName"]);
        Assert.Empty(OwnerAttentionRules.Taxi(Taxi(visibleRoutes: 5, routesLimit: 5)));
    }

    [Fact]
    public void Codes_and_priorities_match_doc_6_9()
    {
        Assert.Equal(11, OwnerAttentionCodes.StayPriority.Count);
        Assert.Equal(11, OwnerAttentionCodes.TaxiPriority.Count);
        Assert.Equal(10, OwnerAttentionCodes.StayPriority[OwnerAttentionCodes.ShareListing]);
        Assert.Equal(11, OwnerAttentionCodes.StayPriority[OwnerAttentionCodes.AddShareDetails]);
        Assert.Equal(5, OwnerAttentionCodes.StayPriority[OwnerAttentionCodes.NotificationsOff]);
        Assert.Equal(3, OwnerAttentionCodes.TaxiPriority[OwnerAttentionCodes.NotificationsOff]);
    }
}

public class OwnerStatusLineRulesTests
{
    [Fact]
    public void Paused_wins_over_everything()
        => Assert.Equal(OwnerStatusLineCodes.Paused, OwnerStatusLineRules.Choose(true, false, true, 3, 2, true, true, "Mukteshwar")!.Code);

    [Fact]
    public void Plans_started_wins_over_new_enquiries()
        => Assert.Equal(OwnerStatusLineCodes.PlansStarted, OwnerStatusLineRules.Choose(true, true, true, 3, 2, true, true, "Mukteshwar")!.Code);

    [Fact]
    public void New_need_reply_wins_over_check_ins()
    {
        var line = OwnerStatusLineRules.Choose(true, true, false, 2, 1, false, false, null)!;
        Assert.Equal(OwnerStatusLineCodes.NewNeedReply, line.Code);
        Assert.Equal(2, line.Args["count"]);
    }

    [Fact]
    public void Check_ins_this_week_before_featured()
        => Assert.Equal(OwnerStatusLineCodes.CheckInsThisWeek, OwnerStatusLineRules.Choose(true, true, false, 0, 2, true, true, "M")!.Code);

    [Fact]
    public void Featured_only_on_a_paid_featured_approved_listing()
    {
        var line = OwnerStatusLineRules.Choose(true, true, false, 0, 0, true, true, "Mukteshwar")!;
        Assert.Equal(OwnerStatusLineCodes.Featured, line.Code);
        Assert.Equal("Mukteshwar", line.Args["city"]);
        Assert.Null(OwnerStatusLineRules.Choose(true, true, false, 0, 0, false, true, "Mukteshwar"));
    }

    [Fact]
    public void A_listing_under_review_shows_no_paused_line()
        => Assert.Null(OwnerStatusLineRules.Choose(false, false, false, 0, 0, false, false, null));
}

public class WeeklyNumbersTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(9L, null)]
    [InlineData(10L, 10)]
    [InlineData(412L, 412)]
    public void Under_ten_or_unknown_is_null(long? value, int? expected)
        => Assert.Equal(expected, OwnerWeeklyRules.Shown(value));

    [Fact]
    public void This_week_is_the_seven_full_ist_days_before_today()
    {
        Assert.Equal(new DateOnly(2026, 11, 14), OwnerWeeklyRules.From(Be4Facts.Today));
        Assert.Equal(new DateOnly(2026, 11, 20), OwnerWeeklyRules.To(Be4Facts.Today));
    }
}

public class OwnerPlanCardStateTests
{
    [Theory]
    [InlineData(false, "Prepaid", null, true, false, OwnerPlanCardStates.PaymentOff)]
    [InlineData(true, "AdminGrant", null, false, false, OwnerPlanCardStates.AdminGrant)]
    [InlineData(true, "Trial", "SUBSCRIPTION_STATE_ACTIVE", true, false, OwnerPlanCardStates.Trial)]
    [InlineData(true, "Subscription", "SUBSCRIPTION_STATE_IN_GRACE_PERIOD", true, false, OwnerPlanCardStates.Grace)]
    [InlineData(true, "Subscription", "SUBSCRIPTION_STATE_CANCELED", false, false, OwnerPlanCardStates.Cancelled)]
    [InlineData(true, "Subscription", "SUBSCRIPTION_STATE_ACTIVE", true, false, OwnerPlanCardStates.Plus)]
    [InlineData(true, null, "SUBSCRIPTION_STATE_ON_HOLD", false, false, OwnerPlanCardStates.OnHold)]
    [InlineData(true, null, null, false, true, OwnerPlanCardStates.Ended)]
    [InlineData(true, null, null, false, false, OwnerPlanCardStates.Free)]
    public void State_follows_the_plan_row_and_play(bool payment, string? source, string? play, bool autoRenew, bool ended, string expected)
        => Assert.Equal(expected, OwnerPlanCardStates.Of(payment, source, play, autoRenew, ended));

    [Fact]
    public void Payment_dates_follow_AU_18()
    {
        Assert.Equal(new DateOnly(2026, 12, 1), OwnerHomeShared.PaymentStartsOn(false, new DateOnly(2026, 12, 1), Be4Facts.Today));
        Assert.Null(OwnerHomeShared.PaymentStartsOn(true, new DateOnly(2026, 12, 1), Be4Facts.Today));
        Assert.Null(OwnerHomeShared.PaymentStartsOn(false, Be4Facts.Today, Be4Facts.Today));
        Assert.True(OwnerHomeShared.PlansStartedToday(true, Be4Facts.Now.AddHours(-2), Be4Facts.Today));
        Assert.False(OwnerHomeShared.PlansStartedToday(true, Be4Facts.Now.AddDays(-1), Be4Facts.Today));
        Assert.False(OwnerHomeShared.PlansStartedToday(false, Be4Facts.Now, Be4Facts.Today));
    }

    [Fact]
    public void Limits_dto_carries_every_plan_key()
    {
        var limits = PlanEntitlementCompute(enabled: false);
        var dto = OwnerHomeShared.LimitsDto(limits);
        Assert.Equal(8, dto.Photos);
        Assert.Equal(6, dto.RoomTypes);
        Assert.Equal(50, dto.CalendarEntries);
        Assert.True(dto.Reminders);
        Assert.Equal(0, dto.Priority);
    }

    [Fact]
    public void Trial_is_offered_only_while_payment_is_on_and_unused()
    {
        var snapshot = OwnerPlanCatalog.Seeded();
        var off = new PaymentFeatureState(false, 30, null, PaymentProviders.Razorpay);
        var on = new PaymentFeatureState(true, 30, null, PaymentProviders.Razorpay);
        Assert.False(OwnerHomeShared.TrialAvailable(snapshot, off, ListingKinds.Stay, new OwnerHomeShared.PlanExtrasRow()));
        Assert.True(OwnerHomeShared.TrialAvailable(snapshot, on, ListingKinds.Stay, new OwnerHomeShared.PlanExtrasRow()));
        Assert.False(OwnerHomeShared.TrialAvailable(snapshot, on, ListingKinds.Stay, new OwnerHomeShared.PlanExtrasRow { AnyPlan = true }));
    }

    private static PlanLimits PlanEntitlementCompute(bool enabled)
        => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), new PaymentFeatureState(enabled, 30, null, PaymentProviders.Play),
            ListingKinds.Stay, null);
}

public class OwnerUpcomingTests
{
    [Fact]
    public void Empty_window_gives_no_rows_and_zero_counts()
    {
        var limits = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(),
            new PaymentFeatureState(true, 30, null, PaymentProviders.Play), ListingKinds.Stay, null);
        var window = OwnerCalendarWindow.Of(limits, Be4Facts.Today);
        var (dto, checkIns) = OwnerHomeShared.BuildUpcoming([new OwnerHomeShared.UpcomingRow { InWindow = 0 }], limits, window);
        Assert.Empty(dto.Rows);
        Assert.Equal(0, dto.MoreInWindowCount);
        Assert.Equal(15, dto.WindowLabelDays);
        Assert.Equal(0, checkIns);
        Assert.NotNull(dto.Window);
    }

    [Fact]
    public void More_in_window_is_the_window_count_minus_the_rows_shown()
    {
        var limits = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(),
            new PaymentFeatureState(true, 30, null, PaymentProviders.Play), ListingKinds.Stay, null);
        var rows = Enumerable.Range(0, 3).Select(i => new OwnerHomeShared.UpcomingRow
        {
            InWindow = 6, CheckInsWeek = 2, BeyondCount = 1, BeyondFirst = Be4Facts.Today.AddDays(40),
            Id = Guid.NewGuid(), Kind = i == 0 ? CalendarEntryKinds.Auto : CalendarEntryKinds.Manual,
            DateFrom = Be4Facts.Today.AddDays(i), DateTo = Be4Facts.Today.AddDays(i + 1), Label = "Neha", FinalAmount = 9000, AdvanceReceived = 3000,
            PaidInFull = false, Cancelled = false, TravellerUserId = null,
        }).ToList();
        var (dto, checkIns) = OwnerHomeShared.BuildUpcoming(rows, limits, OwnerCalendarWindow.Of(limits, Be4Facts.Today));
        Assert.Equal(3, dto.Rows.Count);
        Assert.Equal(3, dto.MoreInWindowCount);
        Assert.Equal(1, dto.BeyondWindowAppCount);
        Assert.Equal(2, checkIns);
        Assert.Equal(MoneyChips.ToCollect, dto.Rows[0].Chip);
        Assert.Equal(6000, dto.Rows[0].ChipAmount);
        Assert.True(dto.Rows[0].TravellerRemoved);
        Assert.False(dto.Rows[1].TravellerRemoved);
    }

    [Fact]
    public void Taxi_sub_line_reads_vehicle_and_seats()
    {
        Assert.Equal("Maruti Ertiga · 6 seats", TaxiDriverHomeReader.Sub("Maruti Ertiga", 6));
        Assert.Equal("Tempo Traveller · 12+ seats", TaxiDriverHomeReader.Sub("Tempo Traveller", 12));
        Assert.Equal("6 seats", TaxiDriverHomeReader.Sub(null, 6));
        Assert.Null(TaxiDriverHomeReader.Sub(null, null));
    }

    [Fact]
    public void Edit_rejected_strip_lasts_seven_days_and_hides_under_a_new_pending_change()
    {
        Assert.True(StayOwnerHomeReader.StayEditRejectedInWindow(false, PendingEditResults.Rejected, Be4Facts.Now.AddDays(-6), Be4Facts.Now));
        Assert.False(StayOwnerHomeReader.StayEditRejectedInWindow(false, PendingEditResults.Rejected, Be4Facts.Now.AddDays(-8), Be4Facts.Now));
        Assert.False(StayOwnerHomeReader.StayEditRejectedInWindow(true, PendingEditResults.Rejected, Be4Facts.Now.AddDays(-1), Be4Facts.Now));
        Assert.False(StayOwnerHomeReader.StayEditRejectedInWindow(false, PendingEditResults.Approved, Be4Facts.Now.AddDays(-1), Be4Facts.Now));
    }

    [Fact]
    public void Home_dto_of_a_fresh_owner_has_every_field_with_safe_defaults()
    {
        var dto = new OwnerHomeDto();
        Assert.NotNull(dto.Listing);
        Assert.NotNull(dto.Attention);
        Assert.NotNull(dto.Numbers);
        Assert.NotNull(dto.Upcoming);
        Assert.NotNull(dto.Upcoming.Rows);
        Assert.Null(dto.PlanCard);
        Assert.Null(dto.Festival);
    }
}

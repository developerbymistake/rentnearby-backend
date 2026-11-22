using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// LOW fixes of FIX_BACKEND (FB-36, 37, 41, 44, 45, 46, 47, 49, 53, 56, 57, 60, 61, 63, 64), each pinned by a rule test or a source
// guard; the parts that need PostGIS are covered by the role journeys' integration tests (written, not run).
public class OwnerLowFixesJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    // FB-36: another owner's block id answers 404; his own retry stays 204 (one statement tells which).
    [Fact]
    public void Block_delete_tells_foreign_from_gone()
    {
        Assert.Contains("o.\"StayListingId\" <> @l) AS \"Foreign\"", OwnerCalendarSql.BlockDelete);
        var handler = Src("RentNearBy.Api", "Handlers", "Owner", "Be1", "StayBlockedRangeHandlers.cs");
        Assert.Contains("BlockedRangeService.DeleteOutcome.NotHis) return NotFoundResponse(", handler);
    }

    // FB-37: a plan bust also bumps the trip supply generation of each district.
    [Fact]
    public void Plan_bust_moves_the_trip_city_lists()
        => Assert.Contains("TripSupplyCache.BumpAsync(redis, d)", Src("RentNearBy.Infrastructure", "Services", "ListingCacheInvalidator.cs"));

    // FB-41: an announce date while payment is (or is being turned) ON is refused; turning it ON clears a stored date.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Announce_date_only_while_payment_is_off(bool enabledAfter, bool refused)
    {
        var d = new DateOnly(2026, 12, 10);
        Assert.Equal(refused, PaymentAnnounceSwitchRules.RefuseAnnounce(d, null, enabledAfter));
        Assert.False(PaymentAnnounceSwitchRules.RefuseAnnounce(d, d, true)); // the stored date sent back unchanged
        Assert.False(PaymentAnnounceSwitchRules.RefuseAnnounce(null, d, true));
        Assert.True(PaymentAnnounceSwitchRules.ClearsAnnounce(true));
        Assert.Equal(400, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PaymentAlreadyOn));
        Assert.Equal("Payments are already on.", OwnerErrorTypes.MessageOf(OwnerErrorTypes.PaymentAlreadyOn));
    }

    // FB-44 (D-32): first full kept month; one order for the top 3 and the balance list; two keyset texts.
    [Fact]
    public void Month_summary_window_and_balance_order()
    {
        Assert.Equal(new DateOnly(2026, 9, 1), OwnerCalendarRules.FirstFullKeptMonth(new DateOnly(2026, 11, 21)));
        Assert.Equal(new DateOnly(2026, 8, 1), OwnerCalendarRules.FirstFullKeptMonth(new DateOnly(2026, 10, 30))); // 90 days back = 1 Aug
        Assert.Contains("t.\"Bal\" DESC, t.\"Id\" DESC", OwnerCalendarSql.Summary);
        Assert.DoesNotContain("@cb IS NULL", OwnerCalendarSql.BalanceDue);
        Assert.DoesNotContain("@cb", OwnerCalendarSql.BalanceDue);
        Assert.Contains("< (@cb, @cid)", OwnerCalendarSql.BalanceDueAfter);
    }

    // FB-45: the hub "Next" row says when the traveller deleted his account.
    [Fact]
    public void Hub_next_row_carries_traveller_removed()
    {
        Assert.True(new RentNearBy.Core.DTOs.Owner.StayCalendarRowNextDto(new DateOnly(2026, 12, 1), "", true).TravellerRemoved);
        Assert.Contains("AS \"NextTravellerRemoved\"", StayHubReader.FactsSql);
    }

    // FB-46: one ring rule for Home and the matcher.
    [Theory]
    [InlineData(true, true, 0, true, true)]
    [InlineData(true, false, 0, true, false)]   // notifications off: never "ready"
    [InlineData(false, true, 0, true, false)]   // never tested: not ready on Home (the matcher may still ring, legacy path)
    [InlineData(true, true, 3, true, false)]
    [InlineData(true, true, 0, false, false)]
    public void Home_ready_follows_the_matcher_rule(bool tested, bool notificationsOn, int missed, bool token, bool expected)
    {
        DateTime? at = tested ? DateTime.UtcNow : null;
        Assert.Equal(expected, TaxiRingRules.HomeReady(at, notificationsOn, missed, token));
        if (expected) Assert.True(TaxiRingRules.CanBeRung(at, notificationsOn, missed, token));
    }

    [Fact]
    public void Ring_rule_sql_and_csharp_stay_equal()
    {
        Assert.Contains("(l.\"RingReadyAt\" IS NULL OR l.\"RingNotificationsOn\")", TaxiCandidateQuery.RingRule);
        Assert.Contains($"l.\"RingMissStreak\" < {TaxiDispatchRules.RingMissLimit}", TaxiCandidateQuery.RingRule);
        Assert.Contains("dt.\"IsValid\"", TaxiCandidateQuery.RingRule);
        Assert.True(TaxiRingRules.CanBeRung(null, false, 0, true));
        Assert.Contains("TaxiRingRules.HomeReady(", Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "TaxiDriverHomeReader.cs"));
    }

    // FB-47: a visible route that copies a hidden one is refused with the duplicate words.
    [Fact]
    public void Route_copy_of_a_hidden_route_is_refused()
    {
        var input = new TaxiRouteInput { FromLatitude = 29.39191m, FromLongitude = 79.45421m, ToLatitude = 29.2m, ToLongitude = 79.5m, TripType = "OneWay" };
        Assert.Equal(UpsertTaxiRoutesRequestValidator.Key(29.3919m, 79.4542m, 29.2m, 79.5m, "OneWay"), UpsertTaxiRoutesRequestValidator.DuplicateKey(input));
        var body = Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs");
        Assert.Contains("r.TaxiListingId == taxi.Id && r.HiddenByPlan", body);
        Assert.Contains("return BadRequestResponse(UpsertTaxiRoutesRequestValidator.DuplicateMessage);", body);
    }

    // FB-49: the taxi batch takes the matcher's ring lock per driver (id order) and re-reads live offers before hiding routes.
    [Fact]
    public void Taxi_reconcile_batch_takes_the_ring_locks_first()
    {
        Assert.Contains("hashtextextended('taxi-ring:' || s.x::text, 0)", PlanLimitsReconcile.RingLocksSql);
        Assert.Contains("ORDER BY x", PlanLimitsReconcile.RingLocksSql);
        Assert.Contains("hashtextextended('taxi-ring:' || @driver::text, 0)", RentNearBy.Infrastructure.Services.TaxiRequests.Matching.MatcherSql.LockDriverRing);
        var src = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        var batch = src[src.IndexOf("private async Task<(int Changed, int Notified, List<BatchRow> Raced)> ApplyBatchAsync(", StringComparison.Ordinal)..];
        Assert.True(batch.IndexOf("RingLocksSql", StringComparison.Ordinal) < batch.IndexOf("FOR UPDATE", StringComparison.Ordinal));
        Assert.Contains("single.AddRange(raced);", src);
    }

    // FB-53 (D-31): a detail view counts once per viewer per stay per day; lists count the first 20; detail routes are limited.
    [Fact]
    public void Shown_counter_dedupes_and_lists_are_capped()
    {
        Assert.Contains("'NX', 'EX'", OwnerShownCounter.OncePerDayScript);
        Assert.Contains("HINCRBY", OwnerShownCounter.OncePerDayScript);
        Assert.Equal(20, OwnerShownCounter.ListedCounted);
        Assert.Equal("owner:shown:seen:20261122:" + Guid.Empty.ToString("N") + ":ip:1.2.3.4",
            OwnerShownCounter.SeenKey(new DateOnly(2026, 11, 22), Guid.Empty, "ip:1.2.3.4"));
        Assert.Equal(120, StayHandlers.DetailRateLimitMax);
        var src = Src("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        Assert.Equal(2, src.Split("RateLimited(rateLimiter, httpContext, DetailRateLimitPrefix, DetailRateLimitMax)").Length - 1);
    }

    // FB-56: approve re-checks that the stored district and city still exist and belong together.
    [Fact]
    public void Approve_rechecks_the_place()
    {
        Assert.Contains("c.\"DistrictId\" = @district", StayPendingEditService.PlaceValidSql);
        var src = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayPendingEditService.cs");
        Assert.True(src.IndexOf("PlaceStillValidAsync(stay, pending, c)", StringComparison.Ordinal) < src.IndexOf("if (reason == null) Apply(stay, pending);", StringComparison.Ordinal));
    }

    // FB-57 (D-17): each new admin action writes one ids-only log line.
    [Theory]
    [InlineData("Owner/Be2/AdminFestivalHandlers.cs", "festival.create", "festival.update", "festival.delete")]
    [InlineData("Owner/Be3/AdminStayEditHandlers.cs", "stayedit.approve", "stayedit.reject")]
    [InlineData("AdminUserPlanHandlers.cs", "plan.grant", "plan.revoke")]
    [InlineData("AdminHandlers.cs", "payment.on", "payment.off")]
    public void Admin_actions_leave_a_log_line(string file, params string[] actions)
    {
        var src = Src(["RentNearBy.Api", "Handlers", .. file.Split('/')]);
        foreach (var a in actions) Assert.Contains($"\"{a}\"", src);
        Assert.Contains("AdminActionLog.Write(", src);
        Assert.DoesNotContain("PhoneNumber", Src("RentNearBy.Api", "Handlers", "AdminActionLog.cs"));
    }

    // FB-60: change requests read once and only for bookings still ahead.
    [Fact]
    public void Stay_home_change_requests_are_bounded()
    {
        var sql = StayOwnerHomeReader.WorkSql;
        Assert.StartsWith("WITH chreq AS", sql.TrimStart());
        Assert.Contains("q.\"CheckOut\" >= @today", sql);
        Assert.Contains("(SELECT count(*) FROM chreq)", sql);
        Assert.Contains("e.\"CheckOut\" >= @today)\n", sql.Replace("\r\n", "\n"));
    }

    // FB-61: a quiet full pass is not repeated within the hour.
    [Fact]
    public void Announce_job_skips_a_quiet_kind_for_an_hour()
    {
        var now = new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc);
        Assert.False(OwnerPlansAnnounceJob.QuietSkip(null, now));
        Assert.True(OwnerPlansAnnounceJob.QuietSkip(now.AddMinutes(-30), now));
        Assert.False(OwnerPlansAnnounceJob.QuietSkip(now.AddMinutes(-61), now));
    }

    // FB-63: the payment state carries the announce facts; the owner job list is bounded and joins the stay once.
    [Fact]
    public void Repeated_reads_are_gone()
    {
        var state = new RentNearBy.Core.Interfaces.PaymentFeatureState(true, 30, null, "Play", new DateOnly(2026, 12, 1), DateTime.UtcNow);
        Assert.NotNull(state.PaymentEnabledAt);
        Assert.Contains("flag?.PaymentEnabledAt", Src("RentNearBy.Infrastructure", "Services", "PaymentFeatureReader.cs"));
        var jobs = Src("RentNearBy.Infrastructure", "Repositories", "StayJobRepository.cs");
        Assert.Contains(".Take(OwnerListMax)", jobs);
        Assert.DoesNotContain("context.StayListings.Where(s => s.Id == j.StayListingId)", jobs);
    }

    // FB-64: the single-instance rule is written down.
    [Fact]
    public void Single_instance_rule_is_in_the_runbook()
        => Assert.Contains("Single API instance rule", Src("SCALE_RUNBOOK.md"));
}

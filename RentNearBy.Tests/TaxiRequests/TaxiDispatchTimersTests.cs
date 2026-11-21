using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Tests.TaxiRequests;

// Owner rows O5 (ask more only), O7 (choose reminders) and O12 (route delete). DB behaviour is in
// TaxiDispatchTimersIntegrationTests and TaxiMatcherPassIntegrationTests (NOT RUN without PostGIS).
public class TaxiDispatchTimersTests
{
    private static readonly Guid Trip = Guid.Parse("7b1c2f0e-5a44-4c1e-9d3a-0f6f2a1b9c01");
    private static readonly Guid Request = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");

    // O5
    [Fact]
    public void Matcher_never_loads_or_sweeps_an_exhausted_request()
    {
        Assert.Contains("r.\"Status\" IN ('Open', 'Collecting') AND r.\"QueueExhaustedAt\" IS NULL", MatcherSql.LoadRequests);
        Assert.Contains("r.\"QueueExhaustedAt\" IS NULL", MatcherSql.DueCities);
        Assert.Contains("q.\"QueueExhaustedAt\" IS NULL", MatcherSql.CitiesOfDrivers);
    }

    [Fact]
    public void Only_ask_more_and_a_withdraw_reopen_clear_the_exhausted_mark()
    {
        Assert.Contains("\"QueueExhaustedAt\" = NULL", TaxiRequestService.AskMoreSql);
        foreach (var (from, to) in new[] { (RequestStates.Selected, RequestStates.Open), (RequestStates.Selected, RequestStates.Collecting), (RequestStates.Booked, RequestStates.Open), (RequestStates.Collecting, RequestStates.Open) })
        {
            var sql = TaxiRequestStoreSql.Reopen(from, to);
            Assert.Contains("\"QueueExhaustedAt\" = CASE WHEN @extra > 0 THEN NULL ELSE r.\"QueueExhaustedAt\" END", sql);
            Assert.DoesNotContain("\"QueueExhaustedAt\" = NULL", sql);
        }
    }

    // An exhausted request that still lacks accepts always offers ask more, even with 0 live accepts or the cap of 9 set:
    // it is the only way it rings again.
    [Theory]
    [InlineData("Open", 2, 3, true, true)]
    [InlineData("Open", 1, 6, true, true)]
    [InlineData("Open", 1, 6, false, false)]
    [InlineData("Open", 8, 9, true, true)]
    [InlineData("Open", 8, 9, false, false)]
    [InlineData("Collecting", 2, 9, true, true)]
    [InlineData("Open", 9, 9, true, false)]
    [InlineData("Selected", 1, 6, true, false)]
    [InlineData("Expired", 1, 6, true, false)]
    public void Exhausted_request_offers_ask_more_while_accepts_are_missing(string state, int accepted, int target, bool exhausted, bool can)
        => Assert.Equal(can, TaxiDispatchRules.CanAskMore(state, accepted, target, exhausted));

    [Theory]
    [InlineData(2, 3, 5)]
    [InlineData(1, 6, 6)]
    [InlineData(8, 9, 9)]
    [InlineData(7, 3, 9)]
    public void Ask_more_never_lowers_the_target(int accepted, int target, int expected)
        => Assert.Equal(expected, TaxiDispatchRules.AskMoreTarget(accepted, target));

    [Fact]
    public void Ask_more_sql_follows_the_rule()
    {
        var sql = TaxiRequestService.AskMoreSql;
        Assert.Contains("\"AcceptTarget\" = GREATEST(\"AcceptTarget\", LEAST(9, \"AcceptedCount\" + 3))::smallint", sql);
        Assert.Contains("OR (\"QueueExhaustedAt\" IS NOT NULL AND \"AcceptedCount\" < \"AcceptTarget\")", sql);
        var service = Body(Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestService.cs"), "AskMoreAsync");
        Assert.Contains("TaxiDispatchRules.CanAskMore(locked.Status, locked.AcceptedCount, locked.AcceptTarget, locked.QueueExhaustedAt is not null)", service);
        Assert.Contains("TaxiDispatchRules.CanAskMore(r.Status, r.AcceptedCount, r.AcceptTarget, r.QueueExhaustedAt is not null)",
            Source("RentNearBy.Api", "Handlers", "TaxiRequests", "TaxiRequestViews.cs"));
    }

    // Reopen never ends a request (so a lapsed selection never adds "All drivers are busy" to its own push), keeps the mark
    // and moves the reminders.
    [Fact]
    public void Reopen_keeps_the_mark_and_never_ends_the_request()
    {
        var body = Body(Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestStore.cs"), "ReopenRequestAsync");
        Assert.DoesNotContain("EndRequestAsync", body);
        Assert.DoesNotContain("AllDriversBusy", body);
        Assert.Contains("if (locked.Status == RequestStates.Selected) await ChooseReminders.ArmAsync(scheduler, db, requestId, locked.DateFrom, ct);", body);
        Assert.Contains("await ChooseReminders.CancelAsync(scheduler, db, requestId, ct);", body);
    }

    [Fact]
    public void Admin_health_does_not_count_an_exhausted_request_as_stuck()
        => Assert.Contains("r.\"QueueWaitStartedAt\" IS NULL AND r.\"QueueExhaustedAt\" IS NULL", MatcherSql.CountStuck);

    // O7
    [Fact]
    public void Reminders_come_after_two_and_twenty_four_hours()
    {
        var now = new DateTime(2026, 12, 1, 4, 0, 0, DateTimeKind.Utc);
        var times = TaxiDispatchRules.ChooseReminderTimes(now, new DateOnly(2026, 12, 20));
        Assert.Equal([(DueActionKinds.ChooseReminderFirst, now.AddHours(2)), (DueActionKinds.ChooseReminderSecond, now.AddHours(24))], times);
    }

    [Fact]
    public void A_reminder_at_or_after_the_stale_expiry_is_never_created()
    {
        var dateFrom = new DateOnly(2026, 12, 20);
        var stale = TaxiDispatchRules.StaleDueAt(dateFrom);
        Assert.Equal([DueActionKinds.ChooseReminderFirst], TaxiDispatchRules.ChooseReminderTimes(stale.AddHours(-10), dateFrom).Select(t => t.Kind));
        Assert.Empty(TaxiDispatchRules.ChooseReminderTimes(stale.AddHours(-2), dateFrom));
        Assert.Empty(TaxiDispatchRules.ChooseReminderTimes(stale.AddHours(-1), dateFrom));
    }

    [Theory]
    [InlineData("Collecting", 2, -1, true)]
    [InlineData("Collecting", 0, -1, false)]
    [InlineData("Selected", 2, -1, false)]
    [InlineData("Booked", 1, -1, false)]
    [InlineData("Expired", 1, -1, false)]
    [InlineData("Collecting", 2, 1, false)]
    public void Reminder_goes_out_only_while_the_traveller_still_has_to_choose(string state, int liveAccepted, int hoursAfterStale, bool due)
    {
        var dateFrom = new DateOnly(2026, 12, 20);
        Assert.Equal(due, TaxiDispatchRules.ChooseReminderDue(state, liveAccepted, TaxiDispatchRules.StaleDueAt(dateFrom).AddHours(hoursAfterStale), dateFrom));
    }

    [Fact]
    public void Reminder_copy_names_the_real_count()
    {
        Assert.Equal("3 drivers replied. Choose one to confirm your taxi.", TaxiRequestNotifications.ChooseDriverBody(3));
        Assert.Equal("1 driver replied. Choose the driver to confirm your taxi.", TaxiRequestNotifications.ChooseDriverBody(1));
        Assert.Equal(NotificationTypes.TaxiChooseDriver, TaxiNoticeTypes.TypeOf(TaxiNoticeKind.ChooseDriver));
    }

    [Fact]
    public async Task Reminder_push_is_an_inbox_row_and_an_outbox_row_to_the_request_status()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);
        ITaxiPushWriter writer = new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"));
        var traveller = Guid.NewGuid();

        await writer.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.ChooseDriver, traveller, Trip, Request, Count: 2));

        var ev = db.ChangeTracker.Entries<NotificationEvent>().Single().Entity;
        Assert.Equal(traveller, ev.TargetUserId);
        Assert.Equal(NotificationTypes.TaxiChooseDriver, ev.Type);
        Assert.Equal("Choose your driver", ev.Title);
        Assert.Equal("2 drivers replied. Choose one to confirm your taxi.", ev.Body);
        Assert.Equal(UserNotificationRoutes.TripTaxiStatus, ev.ActionRoute);
        Assert.Equal(TaxiRequestNotifications.TravellerArgs(Trip, Request), ev.ActionArgumentsJson);
        Assert.Single(db.ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public void Each_reminder_kind_has_its_handler()
    {
        var store = new TaxiRequestStore(null!, null!, null!, null!, null!, null!, NullLogger<TaxiRequestStore>.Instance);
        Assert.Equal(DueActionKinds.ChooseReminderFirst, new ChooseReminderFirstHandler(store, null!).Kind);
        Assert.Equal(DueActionKinds.ChooseReminderSecond, new ChooseReminderSecondHandler(store, null!).Kind);
        Assert.Equal([DueActionKinds.ChooseReminderFirst, DueActionKinds.ChooseReminderSecond], DueActionKinds.ChooseReminders);
        Assert.All(DueActionKinds.ChooseReminders, k => Assert.Contains(k, DueActionKinds.All));
        Assert.All(DueActionKinds.All, k => Assert.True(k.Length <= 24, k));
    }

    [Fact]
    public void First_accept_arms_and_select_cancels_the_reminders()
    {
        var accept = Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiOfferService.cs");
        Assert.Contains("if (request.Status == RequestStates.Open) await ChooseReminders.ArmAsync(scheduler, db, request.Id, request.DateFrom, ct);", accept);

        var select = Body(Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestService.cs"), "SelectAsync");
        var moved = select.IndexOf("TryMoveRequestAsync(db, requestId, [RequestStates.Collecting], RequestStates.Selected", StringComparison.Ordinal);
        var cancel = select.IndexOf("await ChooseReminders.CancelAsync(scheduler, db, requestId, c);", StringComparison.Ordinal);
        Assert.True(moved > 0 && cancel > moved, "Reminders stop once a driver is selected");

        // An ended request cancels every pending kind, the reminders included.
        Assert.Contains("await scheduler.CancelForRequestAsync(db, requestId, DueActionKinds.All, ct);",
            Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestStore.cs"));
    }

    // O12
    [Fact]
    public void Route_offers_query_reads_only_ringing_and_accepted_offers_of_the_driver()
    {
        var sql = TaxiRequestClosure.RouteOffersOfDriverSql;
        Assert.Contains("o.\"DriverListingId\" = @id AND o.\"Status\" IN ('Ringing', 'Accepted')", sql);
        Assert.Contains("r.\"MatchToCityKey\", r.\"MatchFromCityKey\"", sql);
        Assert.DoesNotContain("Selected", sql);
        Assert.DoesNotContain("TaxiBookings", sql);
    }

    [Theory]
    [InlineData(10L, null, true)]
    [InlineData(20L, null, true)]
    [InlineData(30L, null, false)]
    [InlineData(10L, 5L, true)]
    [InlineData(10L, 6L, false)]
    public void Offer_stays_while_another_route_still_serves_it(long matchTo, long? matchFrom, bool stays)
    {
        RouteServeKeys[] routes = [new(5L, 10L), new(7L, 20L)];
        Assert.Equal(stays, TaxiDispatchRules.RoutesStillServe(matchTo, matchFrom, routes));
    }

    // A null key is no city, as in the matcher: it never keeps an offer unless its lookup is still pending in this save.
    [Fact]
    public void Only_a_pending_null_key_keeps_the_offer()
    {
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, null, []));
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, 5, [new(null, 10L)]));
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, 5, [new(5L, null)]));
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, null, [new(null, null)]));
        Assert.True(TaxiDispatchRules.RoutesStillServe(10, 5, [new(null, 10L, FromPending: true)]));
        Assert.True(TaxiDispatchRules.RoutesStillServe(10, 5, [new(5L, null, ToPending: true)]));
        Assert.True(TaxiDispatchRules.RoutesStillServe(10, null, [new(null, null, ToPending: true)]));
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, 5, [new(6L, null, ToPending: true)]));
        Assert.False(TaxiDispatchRules.RoutesStillServe(10, 5, [new(5L, 11L, true, true)]));
    }

    private static readonly TripCityInfo Haldwani = TaxiRouteCityKeyTests.Haldwani;
    private static readonly TripCityInfo Bhimtal = TaxiRouteCityKeyTests.Bhimtal;
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(50);

    private sealed class AnswerResolver(Func<Task<TrustedCityPin>> answer) : ICityResolver
    {
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new NotSupportedException();
        public Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng) => answer();
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(new CityPin(true, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private static readonly AnswerResolver NeverAnswers = new(() => new TaskCompletionSource<TrustedCityPin>().Task);

    private static TaxiRoute RouteAt(TripCityInfo from, TripCityInfo to, bool keyed) => new()
    {
        Id = Guid.NewGuid(), FromLatitude = (decimal)from.Latitude, FromLongitude = (decimal)from.Longitude,
        ToLatitude = (decimal)to.Latitude, ToLongitude = (decimal)to.Longitude,
        FromCityKey = keyed ? from.CityKey : null, ToCityKey = keyed ? to.CityKey : null,
    };

    // The verifier case: the route that matched (to Bhimtal) is deleted, the one left has a null key that was already null
    // and did not move. The matcher could never ring through it, so it keeps nothing.
    [Fact]
    public async Task Deleting_the_matching_route_closes_even_with_an_old_unresolved_route_left()
    {
        var left = RouteAt(Haldwani, Haldwani, keyed: false);
        var (_, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(NeverAnswers, [(left, RouteEndsBefore.Of(left))], new RecordingLogger(), ShortBudget);

        Assert.Equal(new RouteServeKeys(null, null), Assert.Single(keys));
        Assert.False(TaxiDispatchRules.RoutesStillServe(Bhimtal.CityKey, null, keys));
    }

    // Slot 1 changed from Haldwani -> Bhimtal to Haldwani -> a new point, and the lookup did not come back in time: the new
    // end may still be Bhimtal, so the Bhimtal offer stays (the backfill checks it again later).
    [Fact]
    public async Task A_moved_end_whose_lookup_is_late_keeps_the_offer()
    {
        var route = RouteAt(Haldwani, Bhimtal, keyed: true);
        var before = RouteEndsBefore.Of(route);
        route.ToLatitude += 0.01m;
        var (_, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(NeverAnswers, [(route, before)], new RecordingLogger(), ShortBudget);

        Assert.Equal(new RouteServeKeys(Haldwani.CityKey, null, ToPending: true), Assert.Single(keys));
        Assert.True(TaxiDispatchRules.RoutesStillServe(Bhimtal.CityKey, Haldwani.CityKey, keys));
        Assert.Null(route.ToCityKey);
    }

    [Fact]
    public async Task A_moved_end_outside_every_city_closes_the_offer()
    {
        var route = RouteAt(Haldwani, Bhimtal, keyed: true);
        var before = RouteEndsBefore.Of(route);
        route.ToLatitude = 28.6139m;
        route.ToLongitude = 77.2090m;
        var outside = new AnswerResolver(() => Task.FromResult(new TrustedCityPin(true, null, false)));
        var (_, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(outside, [(route, before)], new RecordingLogger(), ShortBudget);

        Assert.Equal(new RouteServeKeys(Haldwani.CityKey, null), Assert.Single(keys));
        Assert.False(TaxiDispatchRules.RoutesStillServe(Bhimtal.CityKey, null, keys));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_moved_end_with_no_city_list_or_a_guessed_city_stays_pending(bool available, bool guessed)
    {
        var route = RouteAt(Haldwani, Bhimtal, keyed: true);
        var before = RouteEndsBefore.Of(route);
        route.ToLatitude += 0.01m;
        var unsure = new AnswerResolver(() => Task.FromResult(new TrustedCityPin(available, guessed ? Bhimtal : null, false)));
        var (_, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(unsure, [(route, before)], new RecordingLogger(), ShortBudget);

        Assert.True(Assert.Single(keys).ToPending);
        Assert.Null(route.ToCityKey);
    }

    [Fact]
    public async Task A_new_route_whose_lookup_is_late_is_pending_and_a_resolved_one_is_not()
    {
        var late = RouteAt(Haldwani, Bhimtal, keyed: false);
        var (_, lateKeys) = await TaxiRouteCityKeys.ResolveForSaveAsync(NeverAnswers, [(late, null)], new RecordingLogger(), ShortBudget);
        Assert.Equal(new RouteServeKeys(null, null, true, true), Assert.Single(lateKeys));

        var found = RouteAt(Haldwani, Bhimtal, keyed: false);
        var trusted = new AnswerResolver(() => Task.FromResult(new TrustedCityPin(true, Bhimtal, true)));
        var (unresolved, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(trusted, [(found, null)], new RecordingLogger(), ShortBudget);
        Assert.Equal(0, unresolved);
        Assert.Equal(new RouteServeKeys(Bhimtal.CityKey, Bhimtal.CityKey), Assert.Single(keys));
        Assert.Equal(Bhimtal.CityKey, found.ToCityKey);
    }

    [Fact]
    public void Route_closure_reads_again_under_the_driver_ring_lock_and_closes_with_route_removed()
    {
        var body = Body(Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestClosure.cs"), "RoutesChangedAsync");
        var firstLocks = body.IndexOf("await LockRequestsAsync(db, Gone(await RouteOffersAsync(db, driverListingId, ct), routes), locked, ct);", StringComparison.Ordinal);
        var ringLock = body.IndexOf("await db.Database.ExecuteSqlRawAsync(MatcherSql.LockDriverRing, [Uuid(\"driver\", driverListingId)], ct);", StringComparison.Ordinal);
        var reread = body.IndexOf("var gone = Gone(await RouteOffersAsync(db, driverListingId, ct), routes);", StringComparison.Ordinal);
        Assert.True(firstLocks > 0 && ringLock > firstLocks && reread > ringLock, "Request locks, then the ring lock, then the offers again");
        Assert.Contains("CloseOfferAsync(db, request, o.Id, RingingOrAccepted, reason, tellTraveller: false, ct)", body);
        Assert.Contains("string reason = OfferEndReasons.RouteRemoved)", Source("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestClosure.cs"));
        Assert.Contains("acc.Freed.Add(driverListingId);", body);
        Assert.Equal("You removed this route", OfferEndReasons.RingEndBody(OfferEndReasons.RouteRemoved));
    }

    [Fact]
    public void Ring_insert_checks_the_route_again()
    {
        Assert.Contains("AND EXISTS (SELECT 1 FROM \"TaxiRequests\" r, \"TaxiListings\" l WHERE r.\"Id\" = @requestId AND l.\"Id\" = @driverId AND", MatcherSql.InsertOffer);
        Assert.Contains(TaxiCandidateQuery.RouteRule, MatcherSql.InsertOffer);
        Assert.True(MatcherSql.InsertOffer.IndexOf("AND EXISTS", StringComparison.Ordinal) < MatcherSql.InsertOffer.IndexOf("ON CONFLICT", StringComparison.Ordinal));
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).Replace("\r\n", "\n");

    private static string Body(string source, string method)
    {
        var start = Regex.Match(source, @"\n    (public|private) async Task(<[^\n]*?>)? " + method + @"\(");
        Assert.True(start.Success, $"{method} not found");
        var next = Regex.Match(source[(start.Index + 1)..], @"\n    (public|private|internal|//)");
        return next.Success ? source.Substring(start.Index, next.Index + 1) : source[start.Index..];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

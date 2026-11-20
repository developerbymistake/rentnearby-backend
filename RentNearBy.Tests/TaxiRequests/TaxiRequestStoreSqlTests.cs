using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRequestStoreSqlTests
{
    private static string InList(IEnumerable<string> states) => string.Join(", ", states.Distinct().Select(s => $"'{s}'"));

    private static int Count(string text, string fragment) => Regex.Matches(text, Regex.Escape(fragment)).Count;

    private static IEnumerable<(string Kind, string To, string[] From)> AllMoves()
    {
        foreach (var kind in TransitionKinds.All)
            foreach (var to in TaxiRequestTransitions.States(kind))
            {
                var from = TaxiRequestTransitions.States(kind).Where(f => TaxiRequestTransitions.CanMove(kind, f, to)).ToArray();
                if (from.Length > 0) yield return (kind, to, from);
            }
    }

    [Fact]
    public void Each_update_repeats_the_from_states()
    {
        var checkedMoves = 0;
        foreach (var (kind, to, from) in AllMoves())
        {
            var list = $"\"Status\" IN ({InList(from)})";
            var sqls = kind switch
            {
                TransitionKinds.Request => [TaxiRequestStoreSql.MoveRequest(from, to)],
                TransitionKinds.Offer => [TaxiRequestStoreSql.MoveOffer(from, to), TaxiRequestStoreSql.CloseOffers(from, to)],
                _ => new[] { TaxiRequestStoreSql.MoveBooking(from, to) },
            };
            foreach (var sql in sqls)
            {
                Assert.Equal(2, Count(sql, list));
                Assert.Contains($"SET \"Status\" = '{to}'", sql);
                checkedMoves++;
            }
            if (kind == TransitionKinds.Request)
                Assert.Contains("\"Version\" = r.\"Version\" + 1", sqls[0]);

            // Each single from state is repeated as well.
            foreach (var f in from)
            {
                var one = kind switch
                {
                    TransitionKinds.Request => TaxiRequestStoreSql.MoveRequest([f], to),
                    TransitionKinds.Offer => TaxiRequestStoreSql.MoveOffer([f], to),
                    _ => TaxiRequestStoreSql.MoveBooking([f], to),
                };
                Assert.Equal(2, Count(one, $"\"Status\" IN ('{f}')"));
            }
        }
        Assert.True(checkedMoves > 20);

        foreach (var (from, to) in new[] { (RequestStates.Selected, RequestStates.Open), (RequestStates.Selected, RequestStates.Collecting), (RequestStates.Booked, RequestStates.Open), (RequestStates.Collecting, RequestStates.Open) })
        {
            var sql = TaxiRequestStoreSql.Reopen(from, to);
            Assert.Equal(2, Count(sql, $"\"Status\" IN ('{from}')"));
            Assert.Contains("\"Version\" = r.\"Version\" + 1", sql);
            Assert.Contains("\"SelectedOfferId\" = NULL", sql);
            Assert.Contains($"LEAST({TaxiDispatchRules.MaxAccepts}, r.\"AcceptTarget\" + @extra)", sql);
        }

        Assert.Contains($"\"Status\" IN ({InList(BookingStates.Active)})", TaxiRequestStoreSql.CancelActiveBookings);
        Assert.Contains("\"Version\" = \"Version\" + 1", TaxiRequestStoreSql.BumpVersion);
        Assert.Contains("FOR UPDATE OF r", TaxiRequestStoreSql.LockRequestOfOffer);
        Assert.Contains("FOR UPDATE OF r", TaxiRequestStoreSql.LockRequestOfBooking);
        Assert.Equal("SELECT 1 FROM \"Trips\" WHERE \"Id\" = @id FOR UPDATE", TaxiRequestStoreSql.LockTrip);
    }

    [Fact]
    public void A_move_the_table_does_not_allow_is_refused_before_any_sql()
    {
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.MoveRequest([RequestStates.Completed], RequestStates.Open));
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.MoveRequest([RequestStates.Open, RequestStates.Booked], RequestStates.Collecting));
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.MoveOffer([OfferStates.Closed], OfferStates.Accepted));
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.MoveBooking([BookingStates.Cancelled], BookingStates.Booked));
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.MoveRequest([], RequestStates.Open));
        Assert.Throws<ArgumentException>(() => TaxiRequestStoreSql.Reopen(RequestStates.Booked, RequestStates.Collecting));
    }

    [Fact]
    public void End_states_stamp_closed_at_and_the_reason()
    {
        var end = TaxiRequestStoreSql.MoveRequest([RequestStates.Open], RequestStates.Cancelled);
        Assert.Contains("\"ClosedAt\" = now()", end);
        Assert.Contains("\"EndReason\" = @endReason", end);
        var mid = TaxiRequestStoreSql.MoveRequest([RequestStates.Open], RequestStates.Collecting);
        Assert.DoesNotContain("ClosedAt", mid);
        Assert.DoesNotContain("EndReason", mid);

        Assert.Contains("\"ClosedAt\" = now()", TaxiRequestStoreSql.MoveOffer([OfferStates.Ringing], OfferStates.Missed));
        Assert.Contains($"interval '{TaxiDispatchRules.ConfirmSeconds} seconds'", TaxiRequestStoreSql.MoveOffer([OfferStates.Accepted], OfferStates.Selected));
        Assert.Contains("\"CancelledBy\" = @cancelledBy", TaxiRequestStoreSql.MoveBooking([BookingStates.Booked], BookingStates.Cancelled));
        Assert.Contains("\"CompletedBy\" = @completedBy", TaxiRequestStoreSql.MoveBooking([BookingStates.InProgress], BookingStates.Completed));
    }

    [Fact]
    public void End_request_only_takes_end_states_and_their_reasons()
    {
        Assert.Equal([RequestStates.Open, RequestStates.Collecting, RequestStates.Selected], TaxiRequestStoreSql.EndableFrom(RequestStates.Expired, RequestEndReasons.NotBooked));
        Assert.Equal([RequestStates.Open, RequestStates.Collecting, RequestStates.Selected], TaxiRequestStoreSql.EndableFrom(RequestStates.Expired, RequestEndReasons.AllDriversBusy));
        Assert.Equal(OfferEndReasons.RequestCancelled, TaxiRequestStoreSql.OfferReasonFor(RequestStates.Cancelled));
        Assert.Equal(OfferEndReasons.RequestExpired, TaxiRequestStoreSql.OfferReasonFor(RequestStates.Expired));
        TaxiRequestStoreSql.EnsureEndReason(RequestStates.Expired, RequestEndReasons.NotBooked);
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxiRequestStoreSql.EnsureEndReason(RequestStates.Expired, RequestEndReasons.TravellerCancelled));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxiRequestStoreSql.EnsureEndReason(RequestStates.Completed, RequestEndReasons.NotBooked));
    }

    [Theory]
    [InlineData(RequestEndReasons.DriverLeftTrip, true)]
    [InlineData(RequestEndReasons.AdminCancelled, true)]
    [InlineData(RequestEndReasons.AccountClosed, true)]
    [InlineData(RequestEndReasons.TravellerCancelled, false)]
    [InlineData(RequestEndReasons.PickupChanged, false)]
    [InlineData(RequestEndReasons.BookingCancelledByTraveller, false)]
    public void A_running_trip_is_cancelled_only_for_its_own_reasons(string reason, bool fromInProgress)
    {
        var from = TaxiRequestStoreSql.EndableFrom(RequestStates.Cancelled, reason);
        Assert.Equal(fromInProgress, from.Contains(RequestStates.InProgress));
        Assert.Equal([RequestStates.Open, RequestStates.Collecting, RequestStates.Selected, RequestStates.Booked],
            from.Where(s => s != RequestStates.InProgress));
        // The SQL built from that list repeats it, so a wrong state can never be cancelled by a careless caller.
        var sql = TaxiRequestStoreSql.MoveRequest(from, RequestStates.Cancelled);
        Assert.Equal(fromInProgress, sql.Contains($"'{RequestStates.InProgress}'"));
    }

    [Theory]
    [InlineData(RequestStates.Selected, true, RequestStates.Collecting)]
    [InlineData(RequestStates.Selected, false, RequestStates.Open)]
    [InlineData(RequestStates.Booked, true, RequestStates.Open)]
    [InlineData(RequestStates.Booked, false, RequestStates.Open)]
    [InlineData(RequestStates.Collecting, false, RequestStates.Open)]
    [InlineData(RequestStates.Collecting, true, null)]
    [InlineData(RequestStates.Open, false, null)]
    [InlineData(RequestStates.Cancelled, true, null)]
    [InlineData(RequestStates.InProgress, false, null)]
    public void Reopen_target_follows_live_accepts(string current, bool hasAccepted, string? expected)
        => Assert.Equal(expected, TaxiRequestStoreSql.ReopenTarget(current, hasAccepted));

    // ---------- pushes ----------

    private sealed class RecordingPushWriter : ITaxiPushWriter
    {
        public List<(Guid UserId, string What)> Sent { get; } = [];

        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) { Sent.Add((ring.DriverUserId, "ring")); return Task.CompletedTask; }

        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default)
        {
            Sent.Add((ringEnd.DriverUserId, "ringend:" + ringEnd.Reason));
            return Task.CompletedTask;
        }

        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default)
        {
            Sent.Add((notice.UserId, notice.Kind.ToString()));
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Traveller = Guid.NewGuid();

    private static TaxiRequestMove Move(string to) => new(Guid.NewGuid(), Guid.NewGuid(), Traveller, RequestStates.Booked, to, 7, 210000123);

    private static TaxiClosedOffer Offer(string from) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), from);

    [Fact]
    public async Task End_request_writes_one_push_per_affected_driver()
    {
        var ringing = Offer(OfferStates.Ringing);
        var accepted = Offer(OfferStates.Accepted);
        var selected = Offer(OfferStates.Selected);
        var booking = new TaxiCancelledBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var writer = new RecordingPushWriter();

        await TaxiRequestStore.WriteEndPushesAsync(writer, null!, Move(RequestStates.Cancelled), OfferEndReasons.RequestCancelled,
            [ringing, accepted, selected], booking, BookingCancelledBy.Traveller, RequestEndReasons.TravellerCancelled, CancellationToken.None);

        Assert.Equal(4, writer.Sent.Count);
        Assert.Equal(4, writer.Sent.Select(s => s.UserId).Distinct().Count());
        Assert.Contains((ringing.DriverUserId, "ringend:" + OfferEndReasons.RequestCancelled), writer.Sent);
        Assert.Contains((accepted.DriverUserId, nameof(TaxiNoticeKind.RequestClosed)), writer.Sent);
        Assert.Contains((selected.DriverUserId, nameof(TaxiNoticeKind.RequestClosed)), writer.Sent);
        Assert.Contains((booking.DriverUserId, nameof(TaxiNoticeKind.BookingCancelled)), writer.Sent);
        Assert.DoesNotContain(writer.Sent, s => s.UserId == Traveller);
    }

    [Theory]
    [InlineData(RequestEndReasons.AllDriversBusy, nameof(TaxiNoticeKind.RequestEnded))]
    [InlineData(RequestEndReasons.NotBooked, nameof(TaxiNoticeKind.RequestEnded))]
    [InlineData(RequestEndReasons.NoReadyDriver, nameof(TaxiNoticeKind.RequestEnded))]
    [InlineData(RequestEndReasons.AdminCancelled, nameof(TaxiNoticeKind.RequestEnded))]
    [InlineData(RequestEndReasons.DriverLeftTrip, nameof(TaxiNoticeKind.DriverWithdrew))]
    [InlineData(RequestEndReasons.TravellerCancelled, null)]
    [InlineData(RequestEndReasons.PickupChanged, null)]
    [InlineData(RequestEndReasons.BookingCancelledByTraveller, null)]
    [InlineData(RequestEndReasons.AccountClosed, null)]
    public async Task Traveller_hears_only_about_ends_he_did_not_cause(string reason, string? expected)
    {
        var writer = new RecordingPushWriter();
        await TaxiRequestStore.WriteEndPushesAsync(writer, null!, Move(RequestStates.Cancelled), OfferEndReasons.RequestCancelled,
            [], null, null, reason, CancellationToken.None);
        var toTraveller = writer.Sent.Where(s => s.UserId == Traveller).Select(s => s.What).ToList();
        if (expected is null) Assert.Empty(toTraveller);
        else Assert.Equal([expected], toTraveller);
    }

    [Fact]
    public async Task Driver_who_left_the_trip_gets_no_booking_cancelled_push()
    {
        var booking = new TaxiCancelledBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var writer = new RecordingPushWriter();
        await TaxiRequestStore.WriteEndPushesAsync(writer, null!, Move(RequestStates.Cancelled), OfferEndReasons.RequestCancelled,
            [], booking, BookingCancelledBy.Driver, RequestEndReasons.DriverLeftTrip, CancellationToken.None);
        Assert.DoesNotContain(writer.Sent, s => s.UserId == booking.DriverUserId);
        Assert.Equal([(Traveller, nameof(TaxiNoticeKind.DriverWithdrew))], writer.Sent);
    }

    private sealed class RecordingScheduler : IDueActionScheduler
    {
        public List<string> Calls { get; } = [];
        public Task AddAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, TimeSpan delay, CancellationToken ct = default) { Calls.Add("add"); return Task.CompletedTask; }
        public Task AddAtAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, DateTime dueAtUtc, CancellationToken ct = default) { Calls.Add("add"); return Task.CompletedTask; }

        public Task CancelAsync(ApplicationDbContext db, string kind, Guid subjectId, CancellationToken ct = default)
        {
            Calls.Add($"cancel:{kind}:{subjectId}");
            return Task.CompletedTask;
        }

        public Task CancelForRequestAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
        {
            Calls.Add($"request:{requestId}:{string.Join(",", kinds.OrderBy(k => k))}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task End_request_cancels_every_timer_of_the_request_its_offers_and_its_booking()
    {
        var move = Move(RequestStates.Cancelled);
        var ringing = Offer(OfferStates.Ringing);
        var selected = Offer(OfferStates.Selected);
        var booking = new TaxiCancelledBooking(Guid.NewGuid(), selected.DriverListingId, Guid.NewGuid());
        var scheduler = new RecordingScheduler();
        var writer = new RecordingPushWriter();

        var result = await TaxiRequestStore.FinishEndAsync(writer, scheduler, null!, move, [ringing, selected], booking,
            BookingCancelledBy.Traveller, RequestEndReasons.TravellerCancelled, CancellationToken.None);

        string[] expected =
        [
            $"request:{move.RequestId}:{string.Join(",", DueActionKinds.All.OrderBy(k => k))}",
            $"cancel:{DueActionKinds.OfferExpire}:{ringing.OfferId}",
            $"cancel:{DueActionKinds.ConfirmExpire}:{ringing.OfferId}",
            $"cancel:{DueActionKinds.OfferExpire}:{selected.OfferId}",
            $"cancel:{DueActionKinds.ConfirmExpire}:{selected.OfferId}",
            $"cancel:{DueActionKinds.BookingStart}:{booking.BookingId}",
            $"cancel:{DueActionKinds.BookingAutoComplete}:{booking.BookingId}",
        ];
        Assert.Equal(expected, scheduler.Calls);
        Assert.Contains((ringing.DriverUserId, "ringend:" + OfferEndReasons.RequestCancelled), writer.Sent);
        Assert.Contains((booking.DriverUserId, nameof(TaxiNoticeKind.BookingCancelled)), writer.Sent);
        Assert.Equal([move.DispatchCityKey], result.Signals.CityKeys);
        Assert.Equal([ringing.DriverListingId, selected.DriverListingId], result.Signals.FreedDriverListingIds);
        Assert.Equal([move.Change], result.Signals.Changes);
        Assert.Same(booking, result.Booking);
    }

    [Fact]
    public async Task Expired_request_without_offers_or_booking_still_cancels_its_request_timers()
    {
        var move = Move(RequestStates.Expired);
        var scheduler = new RecordingScheduler();
        var writer = new RecordingPushWriter();

        var result = await TaxiRequestStore.FinishEndAsync(writer, scheduler, null!, move, [], null, null, RequestEndReasons.NotBooked, CancellationToken.None);

        Assert.Equal([$"request:{move.RequestId}:{string.Join(",", DueActionKinds.All.OrderBy(k => k))}"], scheduler.Calls);
        Assert.Equal([(Traveller, nameof(TaxiNoticeKind.RequestEnded))], writer.Sent);
        Assert.Empty(result.Signals.FreedDriverListingIds);
    }

    // ---------- after commit ----------

    private sealed class Signals(bool fail) : IOutboxSignal, IDueActionsSignal, IMatcherSignal, ITaxiRequestEvents
    {
        public List<string> Calls { get; } = [];

        private void Hit(string what)
        {
            Calls.Add(what);
            if (fail) throw new InvalidOperationException(what);
        }

        void IOutboxSignal.Wake() => Hit("outbox");
        void IDueActionsSignal.Wake() => Hit("due");
        public void MarkDirty(long cityKey) => Hit("city:" + cityKey);
        public void MarkDriverFreed(Guid driverListingId) => Hit("driver:" + driverListingId);

        public Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state)
        {
            Calls.Add("event:" + requestId);
            return fail ? Task.FromException(new InvalidOperationException("event")) : Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger<TaxiRequestStore>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    private static TaxiRequestStore Store(Signals s, ListLogger log)
        => new(new RecordingPushWriter(), new NoScheduler(), s, s, s, s, log);

    private sealed class NoScheduler : IDueActionScheduler
    {
        public Task AddAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAtAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, DateTime dueAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelAsync(ApplicationDbContext db, string kind, Guid subjectId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelForRequestAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> kinds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TaxiClosureResult Result()
    {
        var move = Move(RequestStates.Cancelled);
        return new TaxiClosureResult([move.DispatchCityKey, 42], [Guid.NewGuid(), Guid.NewGuid()], [move.Change, Move(RequestStates.Expired).Change]);
    }

    [Fact]
    public async Task After_commit_failures_never_throw()
    {
        var signals = new Signals(fail: true);
        var log = new ListLogger();

        var ex = await Record.ExceptionAsync(() => Store(signals, log).AfterCommitAsync(Result()));

        Assert.Null(ex);
        // Every step still ran after the earlier ones failed, and each failure was logged.
        Assert.Equal(8, signals.Calls.Count);
        Assert.Equal(8, log.Warnings);
    }

    [Fact]
    public async Task After_commit_wakes_every_signal_once()
    {
        var signals = new Signals(fail: false);
        var log = new ListLogger();
        var result = Result();

        await Store(signals, log).AfterCommitAsync(result);

        Assert.Equal(1, signals.Calls.Count(c => c == "outbox"));
        Assert.Equal(1, signals.Calls.Count(c => c == "due"));
        Assert.Equal(2, signals.Calls.Count(c => c.StartsWith("city:")));
        Assert.Equal(2, signals.Calls.Count(c => c.StartsWith("driver:")));
        Assert.Equal(2, signals.Calls.Count(c => c.StartsWith("event:")));
        Assert.Equal(0, log.Warnings);
    }

    // ---------- candidate query ----------

    [Fact]
    public void Candidate_sql_contains_every_eligibility_rule()
    {
        foreach (var sql in new[] { TaxiCandidateQuery.CandidatesSql, TaxiCandidateQuery.RemainingSql })
        {
            // Static
            Assert.Contains("l.\"Status\" = 'Approved'", sql);
            Assert.Contains("l.\"IsAvailable\"", sql);
            Assert.Contains("NOT l.\"IsDeleted\"", sql);
            Assert.Contains("l.\"RingReadyAt\" IS NULL", sql);
            Assert.Contains("l.\"RingNotificationsOn\"", sql);
            Assert.Contains("l.\"RingMissStreak\" < 3", sql);
            Assert.Contains("dt.\"UserId\" = l.\"UserId\" AND dt.\"IsValid\"", sql);
            Assert.Contains("l.\"DistrictId\" IS NULL OR EXISTS (SELECT 1 FROM \"Districts\" d WHERE d.\"Id\" = l.\"DistrictId\" AND d.\"IsActive\")", sql);
            Assert.Contains("rt.\"ToCityKey\" = r.\"MatchToCityKey\"", sql);
            Assert.Contains("r.\"MatchFromCityKey\" IS NULL OR rt.\"FromCityKey\" = r.\"MatchFromCityKey\"", sql);
            Assert.Contains("l.\"Seats\" >= r.\"Seats\"", sql);
            Assert.Contains("l.\"UserId\" <> r.\"TravellerUserId\"", sql);
            Assert.Contains("ub.\"BlockerId\" = l.\"UserId\" AND ub.\"BlockedId\" = r.\"TravellerUserId\"", sql);
            Assert.Contains("ub.\"BlockerId\" = r.\"TravellerUserId\" AND ub.\"BlockedId\" = l.\"UserId\"", sql);
            // Per request
            Assert.Contains("po.\"RequestId\" = r.\"Id\" AND po.\"DriverListingId\" = l.\"Id\"", sql);
            Assert.Contains("bk.\"Status\" IN ('Booked', 'InProgress')", sql);
            Assert.Contains("bk.\"DateFrom\" <= r.\"DateTo\" AND bk.\"DateTo\" >= r.\"DateFrom\"", sql);
            Assert.Contains("te.\"Kind\" = 'Taxi'", sql);
            Assert.Contains("te.\"Day\" IS NULL AND te.\"Status\" = 'ConfirmedByBoth'", sql);
            // Same legacy range as DriverCalendar.OverlapSql: a row without CheckOut covers CheckIn.
            Assert.Contains("te.\"CheckIn\" <= r.\"DateTo\" AND COALESCE(te.\"CheckOut\", te.\"CheckIn\" + 1) > r.\"DateFrom\"", sql);
            Assert.Contains("COALESCE(e.\"CheckOut\", e.\"CheckIn\" + 1) > @from", DriverCalendar.OverlapSql);
            // Free now: Ringing and Selected block, Accepted does not.
            Assert.Contains("fo.\"Status\" IN ('Ringing', 'Selected')", sql);
            Assert.DoesNotContain("'Accepted'", sql);
        }

        Assert.Contains("l.\"RingMissStreak\" < 3", TaxiCandidateQuery.PoolSql);
        Assert.Contains("FOR UPDATE OF ds SKIP LOCKED", TaxiCandidateQuery.PoolSql);
        Assert.Contains("fo.\"Status\" IN ('Ringing', 'Selected')", TaxiCandidateQuery.PoolSql);
        Assert.Contains(TaxiCandidateQuery.StaticRule, TaxiCandidateQuery.PoolSql);
        Assert.Contains(TaxiCandidateQuery.StaticRule, TaxiCandidateQuery.EnsureDispatchStatesSql);
        Assert.Contains("ON CONFLICT (\"DriverListingId\", \"CityKey\") DO NOTHING", TaxiCandidateQuery.EnsureDispatchStatesSql);
        Assert.Contains("FILTER (WHERE x.\"Free\")", TaxiCandidateQuery.RemainingSql);
        Assert.Contains("FILTER (WHERE NOT x.\"Free\")", TaxiCandidateQuery.RemainingSql);
    }

    [Fact]
    public void Ring_miss_helpers_are_single_statements()
    {
        foreach (var sql in new[] { TaxiRequestStoreSql.AddRingMiss, TaxiRequestStoreSql.ResetRingMiss })
        {
            Assert.DoesNotContain(";", sql);
            Assert.StartsWith("UPDATE \"TaxiListings\" SET \"RingMissStreak\" =", sql.Trim());
            Assert.Equal(1, Count(sql, "UPDATE"));
            Assert.DoesNotContain("FOR UPDATE", sql);
            Assert.DoesNotContain("SELECT", sql);
            Assert.Contains("WHERE \"Id\" = @id", sql);
        }
        Assert.Contains("\"RingMissStreak\" = \"RingMissStreak\" + 1", TaxiRequestStoreSql.AddRingMiss);
        Assert.Contains("RETURNING \"RingMissStreak\"", TaxiRequestStoreSql.AddRingMiss);
        Assert.Contains("\"RingMissStreak\" = 0 WHERE \"Id\" = @id AND \"RingMissStreak\" > 0", TaxiRequestStoreSql.ResetRingMiss);
    }

    private static readonly Regex RingMissWrite = new(
        @"""RingMissStreak""\s*=(?!=)|\bRingMissStreak\s*(=(?!=)|\+\+|--|[+\-*/]=)|(\+\+|--)\s*[\w.]*\bRingMissStreak\b|SetProperty\(\s*\w+\s*=>\s*\w+\.RingMissStreak");

    [Theory]
    [InlineData("listing.RingMissStreak = 0;", true)]
    [InlineData("listing.RingMissStreak++;", true)]
    [InlineData("listing.RingMissStreak--;", true)]
    [InlineData("++listing.RingMissStreak;", true)]
    [InlineData("listing.RingMissStreak += 1;", true)]
    [InlineData("listing.RingMissStreak -= 1;", true)]
    [InlineData("SET \"RingMissStreak\" = 0", true)]
    [InlineData(".SetProperty(t => t.RingMissStreak, 0)", true)]
    [InlineData("public short RingMissStreak { get; set; }", false)]
    [InlineData("if (listing.RingMissStreak == 3)", false)]
    [InlineData("AND l.\"RingMissStreak\" < 3", false)]
    [InlineData("t.HasCheckConstraint(\"ck\", \"\\\"RingMissStreak\\\" >= 0\");", false)]
    public void Ring_miss_write_guard_sees_every_write_form(string code, bool isWrite)
        => Assert.Equal(isWrite, RingMissWrite.IsMatch(code));

    [Fact]
    public void Ring_miss_counter_is_written_only_by_the_two_helpers()
    {
        var root = RepoRoot();
        var offenders = new[] { "RentNearBy.Api", "RentNearBy.Infrastructure", "RentNearBy.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "TaxiRequestStore.cs")
            .Where(f => RingMissWrite.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);

        var store = File.ReadAllText(Path.Combine(root, "RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestStore.cs"));
        Assert.Equal(2, Regex.Matches(store, @"UPDATE \\?""TaxiListings\\?"" SET \\?""RingMissStreak\\?"" =").Count);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

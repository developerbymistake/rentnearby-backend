using System.Text.Json;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Contract section 7 (ring ack) and the missed rings rule of section 6 (P26), against in-memory steps.
public class TaxiAckTests
{
    private static readonly TaxiAckTokens Tokens = new("unit-test-signing-key-that-is-long-enough-0123456789");
    private static readonly DateTime OfferedAt = new(2026, 12, 10, 9, 41, 8, DateTimeKind.Utc);
    private static readonly DateTime Now = OfferedAt.AddSeconds(30);

    private sealed class FakeAckSteps : ITaxiAckSteps
    {
        public readonly Dictionary<Guid, TaxiAckOffer> Offers = [];
        public readonly Dictionary<Guid, (DateTime At, bool Late)> Stored = [];
        public readonly List<Guid> Resets = [];
        public int Finds;

        public Task<TaxiAckOffer?> FindOfferAsync(Guid offerId, CancellationToken ct)
        {
            Finds++;
            return Task.FromResult(Offers.GetValueOrDefault(offerId));
        }

        // Same condition as EfTaxiAckSteps.RecordFirstSql: while no ack is stored, or a good ack over a stored late one.
        public Task<bool> TryRecordFirstAsync(Guid offerId, DateTime receivedAtUtc, bool late, CancellationToken ct)
        {
            if (Stored.TryGetValue(offerId, out var was) && !(was.Late && !late)) return Task.FromResult(false);
            Stored[offerId] = (receivedAtUtc, late);
            return Task.FromResult(true);
        }

        public Task ResetRingMissAsync(Guid driverListingId, CancellationToken ct)
        {
            Resets.Add(driverListingId);
            return Task.CompletedTask;
        }
    }

    // Same rules as EfDriverReadinessSteps: pool inputs compared before the write, reset only from above 0.
    private sealed class FakeReadinessSteps(Guid? listingId, bool wasReady = false, bool wasNotificationsOn = false) : IDriverReadinessSteps
    {
        public DriverDeviceStateDto? Report;
        public int Streak = TaxiDispatchRules.RingMissLimit;
        public int After;
        private bool _ready = wasReady;
        private bool _notificationsOn = wasNotificationsOn;

        public Task<DriverReportStored?> StoreReportAsync(Guid driverUserId, DriverDeviceStateDto report, DateTime nowUtc, CancellationToken ct)
        {
            if (listingId is not { } id) return Task.FromResult<DriverReportStored?>(null);
            Report = report;
            var changed = DriverReadinessFlow.PoolInputsChanged(_ready, _notificationsOn, report);
            _ready = true;
            _notificationsOn = report.NotificationsOn;
            return Task.FromResult<DriverReportStored?>(new DriverReportStored(id, changed));
        }

        public Task<bool> ResetRingMissAsync(Guid driverListingId, CancellationToken ct)
        {
            var was = Streak > 0;
            Streak = 0;
            return Task.FromResult(was);
        }

        public Task AfterReportAsync(Guid driverListingId)
        {
            After++;
            return Task.CompletedTask;
        }
    }

    private static (FakeAckSteps Steps, Guid OfferId, Guid ListingId) Kit()
    {
        var steps = new FakeAckSteps();
        var offer = Guid.NewGuid();
        var listing = Guid.NewGuid();
        steps.Offers[offer] = new TaxiAckOffer(listing, OfferedAt, OfferedAt.AddSeconds(TaxiDispatchRules.RingSeconds));
        return (steps, offer, listing);
    }

    private static TaxiRingAckDto Body(Guid offerId, bool shown = true, bool late = false, DateTime? receivedAt = null, string? token = null) => new()
    {
        AckToken = token ?? Tokens.Create(offerId), ReceivedAt = receivedAt ?? OfferedAt.AddSeconds(1), Shown = shown, Late = late,
        NotificationsOn = true, RingChannelOn = true, Manufacturer = "Xiaomi", AndroidSdk = 34, AppBuild = 69,
    };

    private static Task<TaxiAckResult> Run(FakeAckSteps steps, Guid offerId, TaxiRingAckDto? body)
        => TaxiAckFlow.RunAsync(steps, Tokens, offerId, body, Now, null, CancellationToken.None);

    [Fact]
    public async Task Ack_with_a_bad_token_is_404()
    {
        var (steps, offer, _) = Kit();

        Assert.Equal(TaxiAckOutcome.NotFound, (await Run(steps, offer, Body(offer, token: Tokens.Create(Guid.NewGuid())))).Outcome);
        Assert.Equal(TaxiAckOutcome.NotFound, (await Run(steps, offer, Body(offer, token: ""))).Outcome);
        Assert.Equal(TaxiAckOutcome.NotFound, (await Run(steps, offer, Body(offer, token: "Qk9fY2xhdWRlX3NhbXBsZQ"))).Outcome);
        Assert.Equal(TaxiAckOutcome.NotFound, (await Run(steps, offer, null)).Outcome);
        // A bad token never reads the offer and changes nothing.
        Assert.Equal(0, steps.Finds);
        Assert.Empty(steps.Stored);
        Assert.Empty(steps.Resets);

        // A good token for an offer that does not exist is 404 too.
        var unknown = Guid.NewGuid();
        Assert.Equal(TaxiAckOutcome.NotFound, (await Run(steps, unknown, Body(unknown))).Outcome);
    }

    [Fact]
    public async Task Second_ack_keeps_the_first_time()
    {
        var (steps, offer, _) = Kit();
        var first = OfferedAt.AddSeconds(2);

        Assert.Equal(TaxiAckOutcome.Stored, (await Run(steps, offer, Body(offer, receivedAt: first))).Outcome);
        Assert.Equal(TaxiAckOutcome.Repeat, (await Run(steps, offer, Body(offer, shown: false, late: true, receivedAt: first.AddSeconds(9)))).Outcome);

        Assert.Equal((first, false), steps.Stored[offer]);
        Assert.Contains("\"PushReceivedAt\" IS NULL", EfTaxiAckSteps.RecordFirstSql);
    }

    [Fact]
    public async Task Late_ack_for_an_ended_ring_is_stored_and_does_not_reset()
    {
        var (steps, offer, _) = Kit();
        var result = await Run(steps, offer, Body(offer, shown: false, late: true));
        Assert.Equal(TaxiAckOutcome.Stored, result.Outcome);
        Assert.False(result.ResetMissedRings);
        Assert.True(steps.Stored[offer].Late);
    }

    [Fact]
    public async Task Ack_after_the_offer_expiry_is_late_and_does_not_reset()
    {
        var (steps, offer, _) = Kit();
        var after = OfferedAt.AddSeconds(TaxiDispatchRules.RingSeconds + 5);
        var result = await TaxiAckFlow.RunAsync(steps, Tokens, offer, Body(offer), after, null, CancellationToken.None);
        Assert.False(result.ResetMissedRings);
        Assert.True(steps.Stored[offer].Late);
    }

    [Fact]
    public async Task Shown_ack_replaces_an_earlier_late_ack_and_resets()
    {
        var (steps, offer, listing) = Kit();
        await Run(steps, offer, Body(offer, shown: false, late: true));
        var result = await Run(steps, offer, Body(offer));
        Assert.True(result.ResetMissedRings);
        Assert.Equal([listing], steps.Resets);
        Assert.False(steps.Stored[offer].Late);
        Assert.Contains("(\"PushLate\" AND NOT @late)", EfTaxiAckSteps.RecordFirstSql);
    }

    [Fact]
    public async Task Ack_shown_in_time_resets_missed_rings()
    {
        var (steps, offer, listing) = Kit();

        var result = await Run(steps, offer, Body(offer));

        Assert.True(result.ResetMissedRings);
        Assert.Equal([listing], steps.Resets);
        Assert.False(steps.Stored[offer].Late);
        // A repeat of the same ack does not reset again.
        await Run(steps, offer, Body(offer));
        Assert.Single(steps.Resets);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Late_or_not_shown_ack_does_not_reset(bool shown, bool late)
    {
        var (steps, offer, _) = Kit();

        var result = await Run(steps, offer, Body(offer, shown, late));

        Assert.Equal(TaxiAckOutcome.Stored, result.Outcome);
        Assert.False(result.ResetMissedRings);
        Assert.Empty(steps.Resets);
        Assert.True(steps.Stored[offer].Late);
    }

    [Fact]
    public async Task Received_time_stays_between_the_ring_and_now()
    {
        var (steps, offer, _) = Kit();
        await Run(steps, offer, Body(offer, receivedAt: OfferedAt.AddDays(3)));
        Assert.Equal(Now, steps.Stored[offer].At);

        var (early, offer2, _) = Kit();
        await Run(early, offer2, Body(offer2, receivedAt: OfferedAt.AddHours(-1)));
        Assert.Equal(OfferedAt, early.Stored[offer2].At);
    }

    [Fact]
    public void Ack_sample_parses_into_the_request_dto()
    {
        var text = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "ack_request.json"));
        var body = JsonSerializer.Deserialize<TaxiRingAckDto>(text, TaxiDriverViewsTests.Web)!;
        Assert.False(TaxiAckFlow.IsLate(body));
    }

    private static DriverDeviceStateDto Report(bool ringTested, bool notificationsOn = true, bool ringChannelOn = true) => new()
    {
        AppBuild = 69, NotificationsOn = notificationsOn, RingChannelOn = ringChannelOn, Manufacturer = "Xiaomi", AndroidSdk = 34, RingTested = ringTested,
    };

    [Fact]
    public async Task Device_report_after_a_test_ring_resets_missed_rings()
    {
        var steps = new FakeReadinessSteps(Guid.NewGuid());

        Assert.NotNull(await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: true), Now, CancellationToken.None));

        Assert.Equal(0, steps.Streak);
        Assert.Equal(1, steps.After);
        Assert.Equal(69, steps.Report!.AppBuild);
        var listing = TaxiDriverViewsTests.Listing(Now, true, (short)steps.Streak);
        Assert.True(TaxiDriverViews.BuildReadiness(listing, true, 0).RingReady);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Device_report_without_a_test_ring_does_not_reset(bool ringTested, bool notificationsOn, bool ringChannelOn)
    {
        var steps = new FakeReadinessSteps(Guid.NewGuid());

        await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested, notificationsOn, ringChannelOn), Now, CancellationToken.None);

        Assert.Equal(TaxiDispatchRules.RingMissLimit, steps.Streak);
        Assert.NotNull(steps.Report);
        var listing = TaxiDriverViewsTests.Listing(Now, notificationsOn, (short)steps.Streak);
        Assert.False(TaxiDriverViews.BuildReadiness(listing, true, 0).RingReady);
    }

    [Fact]
    public async Task Device_report_without_a_taxi_listing_changes_nothing()
    {
        var steps = new FakeReadinessSteps(null);
        Assert.Null(await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: true), Now, CancellationToken.None));
        Assert.Equal(0, steps.After);
        Assert.Equal(TaxiDispatchRules.RingMissLimit, steps.Streak);
    }

    [Fact]
    public async Task Repeated_report_with_nothing_changed_does_not_bump_the_cache_or_wake_the_matcher()
    {
        var steps = new FakeReadinessSteps(Guid.NewGuid());
        steps.Streak = 0;

        await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: true), Now, CancellationToken.None);
        Assert.Equal(1, steps.After);

        for (var i = 0; i < 5; i++)
            await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: true), Now.AddMinutes(i), CancellationToken.None);
        Assert.Equal(1, steps.After);

        await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: false, notificationsOn: false), Now, CancellationToken.None);
        Assert.Equal(2, steps.After);
    }

    [Fact]
    public async Task Missed_rings_reset_alone_counts_as_a_pool_change()
    {
        var steps = new FakeReadinessSteps(Guid.NewGuid(), wasReady: true, wasNotificationsOn: true);

        await DriverReadinessFlow.ReportAsync(steps, Guid.NewGuid(), Report(ringTested: true), Now, CancellationToken.None);

        Assert.Equal(0, steps.Streak);
        Assert.Equal(1, steps.After);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void Pool_inputs_change_when_ready_was_null_or_notifications_flip(bool wasReady, bool wasOn, bool nowOn, bool changed)
        => Assert.Equal(changed, DriverReadinessFlow.PoolInputsChanged(wasReady, wasOn, Report(ringTested: false, notificationsOn: nowOn)));

    [Fact]
    public void Readiness_limit_is_per_driver_and_20_per_hour()
    {
        var driver = Guid.NewGuid();
        Assert.Equal($"taxi:readiness:{driver}", TaxiDriverRequestHandlers.ReadinessRateKey(driver));
        Assert.Equal(20, TaxiDispatchRules.ReadinessReportsPerHour);
    }

    [Fact]
    public void Ack_limit_charges_the_ip_only_for_a_bad_token_and_the_offer_for_a_good_one()
    {
        var offer = Guid.NewGuid();
        Assert.Equal(($"taxi:ack:ip:10.0.0.1", 30), TaxiAckHandlers.LimitFor(offer, "10.0.0.1", tokenValid: false));
        Assert.Equal(($"taxi:ack:offer:{offer}", 5), TaxiAckHandlers.LimitFor(offer, "10.0.0.1", tokenValid: true));
    }

    [Fact]
    public void Test_ring_is_the_ring_shape_with_no_offer_and_30_seconds()
    {
        var user = Guid.NewGuid();
        var row = TaxiDriverRequestHandlers.TestRingOutbox(user, Now);
        using var doc = JsonDocument.Parse(row.Payload);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(TaxiPushKinds.RingTest, data.GetProperty("kind").GetString());
        Assert.Equal("", data.GetProperty("offerId").GetString());
        Assert.Equal(TaxiRingPayload.Instant(Now.AddSeconds(TaxiDispatchRules.TestRingSeconds)), data.GetProperty("expiresAt").GetString());
        Assert.Equal("taxi_request_ring", data.GetProperty("notification_type").GetString());
        Assert.Equal(user.ToString("D"), data.GetProperty("driverUserId").GetString());
        Assert.Equal("TaxiRequestRing", doc.RootElement.GetProperty("kind").GetString());
        var ringKeys = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "push_ring.json"));
        using var ring = JsonDocument.Parse(ringKeys);
        Assert.Equal(ring.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).Order(),
            data.EnumerateObject().Select(p => p.Name).Order());
    }
}

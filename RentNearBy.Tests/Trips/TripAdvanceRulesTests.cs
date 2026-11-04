namespace RentNearBy.Tests.Trips;

public class TripAdvanceRulesTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

    private static readonly DateOnly CheckIn = new(2026, 11, 1);
    private static readonly DateTime NoonIst = Utc(2026, 11, 1, 6, 30);

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(1, TripAdvanceRules.HoldMinHours);
        Assert.Equal(6, TripAdvanceRules.HoldDefaultHours);
        Assert.Equal(48, TripAdvanceRules.HoldMaxHours);
        Assert.Equal(12, TripAdvanceRules.HoldCheckInCutoffHourIst);
        Assert.Equal(60, TripAdvanceRules.ReminderMinutes);
        Assert.Equal(60, TripAdvanceRules.UpiMax);
        Assert.Equal(5, TripAdvanceRules.UpiMin);
    }

    [Theory]
    [InlineData("name@bank")]
    [InlineData("a.b-c_d@okhdfc")]
    [InlineData("98765@ybl")]
    [InlineData("ab@cd")]
    [InlineData("  name@bank  ")]
    [InlineData("Name.Surname@OkSBI")]
    public void Valid_upi_ids(string id) => Assert.True(TripAdvanceRules.IsValidUpi(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name")]
    [InlineData("@bank")]
    [InlineData("name@")]
    [InlineData("a@b")]
    [InlineData("name@bank1")]
    [InlineData("na me@bank")]
    [InlineData("name@@bank")]
    [InlineData("name@bank.com")]
    public void Invalid_upi_ids(string? id) => Assert.False(TripAdvanceRules.IsValidUpi(id));

    [Fact]
    public void Upi_length_edges()
    {
        var at60 = new string('a', 50) + "@" + new string('b', 9);
        var at61 = new string('a', 50) + "@" + new string('b', 10);
        Assert.Equal(60, at60.Length);
        Assert.Equal(61, at61.Length);
        Assert.True(TripAdvanceRules.IsValidUpi(at60));
        Assert.False(TripAdvanceRules.IsValidUpi(at61));
        Assert.False(TripAdvanceRules.IsValidUpi("a@bc"));
        Assert.True(TripAdvanceRules.IsValidUpi("ab@cd"));
    }

    [Fact]
    public void HoldCap_is_check_in_noon_ist_when_it_comes_first()
    {
        var now = Utc(2026, 10, 31);
        Assert.Equal(NoonIst, TripAdvanceRules.HoldCap(CheckIn, now));
    }

    [Fact]
    public void HoldCap_is_48_hours_when_check_in_is_far()
    {
        var now = Utc(2026, 10, 25, 10);
        Assert.Equal(now.AddHours(48), TripAdvanceRules.HoldCap(CheckIn, now));
    }

    [Fact]
    public void HoldCap_switches_at_the_exact_boundary()
    {
        var boundary = NoonIst.AddHours(-48);
        Assert.Equal(NoonIst, TripAdvanceRules.HoldCap(CheckIn, boundary));
        Assert.Equal(NoonIst, TripAdvanceRules.HoldCap(CheckIn, boundary.AddMinutes(1)));
        Assert.Equal(boundary.AddMinutes(-1).AddHours(48), TripAdvanceRules.HoldCap(CheckIn, boundary.AddMinutes(-1)));
    }

    [Fact]
    public void HoldAllowed_lower_edge_is_one_hour()
    {
        var now = Utc(2026, 10, 25, 10);
        Assert.True(TripAdvanceRules.HoldAllowed(now.AddHours(1), CheckIn, now));
        Assert.False(TripAdvanceRules.HoldAllowed(now.AddHours(1).AddSeconds(-1), CheckIn, now));
        Assert.False(TripAdvanceRules.HoldAllowed(now, CheckIn, now));
    }

    [Fact]
    public void HoldAllowed_upper_edge_is_the_cap()
    {
        var far = Utc(2026, 10, 25, 10);
        var farCap = TripAdvanceRules.HoldCap(CheckIn, far);
        Assert.True(TripAdvanceRules.HoldAllowed(farCap, CheckIn, far));
        Assert.False(TripAdvanceRules.HoldAllowed(farCap.AddSeconds(1), CheckIn, far));

        var near = Utc(2026, 10, 31, 20);
        Assert.True(TripAdvanceRules.HoldAllowed(NoonIst, CheckIn, near));
        Assert.False(TripAdvanceRules.HoldAllowed(NoonIst.AddSeconds(1), CheckIn, near));
    }

    [Fact]
    public void HoldAllowed_is_false_when_the_cap_is_inside_the_minimum()
    {
        var now = NoonIst.AddMinutes(-30);
        Assert.False(TripAdvanceRules.HoldAllowed(NoonIst, CheckIn, now));
    }

    private static TripEnquiry Proof(DateTime? last, DateTime? hold, DateTime? proofSent = null) => new()
    {
        Status = TripEnquiryStatuses.ConfirmedByBoth,
        AdvanceStatus = TripAdvanceStatuses.ProofSent,
        HoldUntil = hold,
        LastHostReminderAt = last,
        ProofSentAt = proofSent,
    };

    [Fact]
    public void NextHostReminder_is_last_plus_an_hour()
    {
        var now = Utc(2026, 10, 31, 10);
        var e = Proof(now.AddMinutes(-20), now.AddHours(5));
        Assert.Equal(now.AddMinutes(40), TripAdvanceRules.NextHostReminder(e, now));
    }

    [Fact]
    public void NextHostReminder_is_capped_at_hold_until()
    {
        var now = Utc(2026, 10, 31, 10);
        var e = Proof(now.AddMinutes(-10), now.AddMinutes(20));
        Assert.Equal(now.AddMinutes(20), TripAdvanceRules.NextHostReminder(e, now));
    }

    [Fact]
    public void NextHostReminder_falls_back_to_proof_sent_time()
    {
        var now = Utc(2026, 10, 31, 10);
        var e = Proof(null, now.AddHours(5), now.AddMinutes(-15));
        Assert.Equal(now.AddMinutes(45), TripAdvanceRules.NextHostReminder(e, now));
    }

    [Fact]
    public void NextHostReminder_is_null_when_not_waiting()
    {
        var now = Utc(2026, 10, 31, 10);
        Assert.Null(TripAdvanceRules.NextHostReminder(Proof(now.AddMinutes(-5), now), now));
        Assert.Null(TripAdvanceRules.NextHostReminder(Proof(now.AddMinutes(-5), now.AddHours(-1)), now));
        Assert.Null(TripAdvanceRules.NextHostReminder(Proof(now.AddMinutes(-5), null), now));

        var pending = Proof(now.AddMinutes(-5), now.AddHours(3));
        pending.AdvanceStatus = TripAdvanceStatuses.Pending;
        Assert.Null(TripAdvanceRules.NextHostReminder(pending, now));

        var received = Proof(now.AddMinutes(-5), now.AddHours(3));
        received.AdvanceStatus = TripAdvanceStatuses.Received;
        Assert.Null(TripAdvanceRules.NextHostReminder(received, now));

        var failed = Proof(now.AddMinutes(-5), now.AddHours(3));
        failed.Status = TripEnquiryStatuses.Failed;
        Assert.Null(TripAdvanceRules.NextHostReminder(failed, now));
    }

    [Fact]
    public void Dedupe_keys_have_the_contract_format()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var at = Utc(2026, 11, 1, 10, 30);
        Assert.Equal("proof:11111111-2222-3333-4444-555555555555:202611011030:0", TripAdvanceRules.ProofKey(id, at, 0));
        Assert.Equal("proof:11111111-2222-3333-4444-555555555555:202611011030:7", TripAdvanceRules.ProofKey(id, at, 7));
        Assert.Equal("notyet:11111111-2222-3333-4444-555555555555:2026110110", TripAdvanceRules.NotYetKey(id, at));
        Assert.Equal("hold:11111111-2222-3333-4444-555555555555:t", TripAdvanceRules.HoldKey(id, "t"));
        Assert.Equal("hold:11111111-2222-3333-4444-555555555555:o", TripAdvanceRules.HoldKey(id, "o"));
    }

    [Fact]
    public void Keys_differ_per_hour_and_per_side()
    {
        var id = Guid.NewGuid();
        var at = Utc(2026, 11, 1, 10, 30);
        Assert.NotEqual(TripAdvanceRules.ProofKey(id, at, 0), TripAdvanceRules.ProofKey(id, at, 1));
        Assert.NotEqual(TripAdvanceRules.NotYetKey(id, at), TripAdvanceRules.NotYetKey(id, at.AddHours(1)));
        Assert.Equal(TripAdvanceRules.NotYetKey(id, at), TripAdvanceRules.NotYetKey(id, at.AddMinutes(20)));
        Assert.NotEqual(TripAdvanceRules.HoldKey(id, "t"), TripAdvanceRules.HoldKey(id, "o"));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.FareSent, null, TripDisplayStatuses.FareSent)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, null, TripDisplayStatuses.BookedByYou)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, null, TripDisplayStatuses.ConfirmedByBoth)]
    [InlineData(TripEnquiryStatuses.ConfirmedByTraveller, null, TripDisplayStatuses.ConfirmedByYou)]
    [InlineData(TripEnquiryStatuses.Failed, TripFailReasons.HoldExpired, TripDisplayStatuses.HoldExpired)]
    [InlineData(TripEnquiryStatuses.Failed, TripFailReasons.DriverDenied, TripDisplayStatuses.NotMe)]
    [InlineData(TripEnquiryStatuses.Failed, TripFailReasons.HostDenied, TripDisplayStatuses.NotMe)]
    [InlineData(TripEnquiryStatuses.NotAvailable, null, TripDisplayStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Asked, null, TripDisplayStatuses.Asked)]
    [InlineData(TripEnquiryStatuses.Offered, null, TripDisplayStatuses.Offered)]
    [InlineData(TripEnquiryStatuses.Closed, null, TripDisplayStatuses.Closed)]
    public void Display_status_rows(string status, string? failReason, string expected)
        => Assert.Equal(expected, TripExecutionRules.DisplayStatus(status, failReason));

    [Fact]
    public void Display_status_wire_values()
    {
        Assert.Equal("fareSent", TripDisplayStatuses.FareSent);
        Assert.Equal("bookedByYou", TripDisplayStatuses.BookedByYou);
        Assert.Equal("holdExpired", TripDisplayStatuses.HoldExpired);
    }

    [Fact]
    public void Booked_and_withdrawable_sets()
    {
        Assert.Equal(
            new[] { TripEnquiryStatuses.ConfirmedByTraveller, TripEnquiryStatuses.ConfirmedByBoth, TripEnquiryStatuses.BookedByTraveller }.OrderBy(x => x),
            TripEnquiryStatuses.Booked.OrderBy(x => x));
        Assert.Equal(
            new[] { TripEnquiryStatuses.Asked, TripEnquiryStatuses.Offered, TripEnquiryStatuses.Countered, TripEnquiryStatuses.FareSent }.OrderBy(x => x),
            TripEnquiryStatuses.Withdrawable.OrderBy(x => x));
        Assert.True(TripEnquiryStatuses.IsBooked(TripEnquiryStatuses.BookedByTraveller));
        Assert.False(TripEnquiryStatuses.IsBooked(TripEnquiryStatuses.FareSent));
        Assert.True(TripEnquiryStatuses.IsWithdrawable(TripEnquiryStatuses.FareSent));
        Assert.False(TripEnquiryStatuses.IsWithdrawable(TripEnquiryStatuses.ConfirmedByBoth));
    }

    [Fact]
    public void Fare_sent_and_booked_by_traveller_are_open()
    {
        Assert.True(TripEnquiryStatuses.IsOpen(TripEnquiryStatuses.FareSent));
        Assert.True(TripEnquiryStatuses.IsOpen(TripEnquiryStatuses.BookedByTraveller));
        Assert.False(TripEnquiryStatuses.IsOpen(TripEnquiryStatuses.NotAvailable));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, TripDriverBuckets.New)]
    [InlineData(TripEnquiryStatuses.FareSent, TripDriverBuckets.Replied)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, TripDriverBuckets.Booked)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripDriverBuckets.Booked)]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripDriverBuckets.Closed)]
    [InlineData(TripEnquiryStatuses.Expired, TripDriverBuckets.Closed)]
    [InlineData(TripEnquiryStatuses.Failed, TripDriverBuckets.Closed)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, TripDriverBuckets.Closed)]
    [InlineData(TripEnquiryStatuses.Closed, TripDriverBuckets.Closed)]
    public void Driver_bucket_of_status(string status, string bucket)
        => Assert.Equal(bucket, TripDriverBuckets.Of(status));

    [Fact]
    public void Driver_buckets_cover_every_status_once()
    {
        Assert.Equal(new[] { "new", "replied", "booked", "closed" }, TripDriverBuckets.All);
        foreach (var s in TripEnquiryStatuses.All)
            Assert.Contains(TripDriverBuckets.Of(s), TripDriverBuckets.All);
    }
}

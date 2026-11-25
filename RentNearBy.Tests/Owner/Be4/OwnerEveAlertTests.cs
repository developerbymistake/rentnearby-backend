using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;

namespace RentNearBy.Tests.Owner.Be4;

// F1 / D11: the evening-before alert for every owner (no plan join, RemindersOn ignored), one push per booking, send once by key,
// nothing in quiet hours.
public class OwnerEveAlertTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateOnly Tomorrow = new(2026, 11, 26);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private static OwnerEveAlertJob Job(string kind, DateTime? utc = null)
    {
        var options = Options.Create(new OwnerFeatureOptions());
        var clock = utc is { } u ? new FixedClock(u) : TimeProvider.System;
        return kind == ListingKinds.Stay
            ? new StayEveAlertJob(null!, null!, options, clock, NullLogger<StayEveAlertJob>.Instance)
            : new TaxiEveAlertJob(null!, null!, options, clock, NullLogger<TaxiEveAlertJob>.Instance);
    }

    [Fact]
    public void Stay_copy_reads_check_in_tomorrow_with_the_guest_and_detail()
    {
        var entry = Guid.NewGuid();
        var p = StayOwnerPushCopy.CheckInTomorrow(User, entry, Tomorrow, "Neha Joshi", "Family Room · 2 guests");
        Assert.Equal("Check-in tomorrow", p.Title);
        Assert.Equal("Neha Joshi (Family Room · 2 guests)", p.Body);
        Assert.Equal($"ownereve:Stay:{entry}:20261126", p.Key);
        Assert.Equal(UserNotificationRoutes.StayUpcoming, p.Route);
        Assert.Equal(NotificationTypes.OwnerBookingTomorrow, p.Type);
        Assert.Equal($"owner_eve_{entry}", p.Tag);
        Assert.Equal(IstTime.IstMidnightUtc(Tomorrow), p.ExpiresAtUtc);
        Assert.Equal("Guest", StayOwnerPushCopy.CheckInTomorrow(User, entry, Tomorrow, " ", null).Body);
    }

    [Fact]
    public void Taxi_copy_names_the_pickup_never_the_traveller()
    {
        var entry = Guid.NewGuid();
        var p = TaxiDriverPushCopy.TripTomorrow(User, entry, Tomorrow, "Kathgodam hub");
        Assert.Equal("Trip tomorrow", p.Title);
        Assert.Equal("Kathgodam hub pickup (all day)", p.Body);
        Assert.Equal($"ownereve:Taxi:{entry}:20261126", p.Key);
        Assert.Equal(UserNotificationRoutes.TaxiUpcoming, p.Route);
        Assert.Equal("Trip (all day)", TaxiDriverPushCopy.TripTomorrow(User, entry, Tomorrow, null).Body);

        var job = Job(ListingKinds.Taxi);
        var pushes = job.Plan([Row($$"""[{"id":"{{entry}}","label":"Seema Rawat","detail":null,"from":"Kathgodam hub"}]""")], Tomorrow);
        Assert.DoesNotContain("Seema", Assert.Single(pushes).Body);
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Sql_has_no_plan_join_and_no_reminders_switch(string kind)
    {
        var job = Job(kind);
        var sql = job.BatchSql;
        Assert.DoesNotContain("Reminders", sql);
        Assert.DoesNotContain("JOIN pv", sql);
        Assert.DoesNotContain("UserPlans", sql);
        Assert.Contains("e.\"Kind\" = 'Auto'", sql);
        Assert.Contains("NOT e.\"Cancelled\"", sql);
        Assert.Contains("e.\"DateFrom\" = @tomorrow", sql);
        Assert.Contains("l.\"Status\" = 'Approved'", sql);
        Assert.Contains("NOT l.\"IsDeleted\"", sql);
        Assert.Contains("u.\"IsActive\"", sql);
        Assert.Contains("\"IsValid\"", sql);
        Assert.Equal(new TimeOnly(18, 0), job.DailyAtIst);
        Assert.Equal(TimeSpan.FromDays(1), job.Interval);
        Assert.Equal(kind == ListingKinds.Stay ? "owner.eve.stay" : "owner.eve.taxi", job.Name);
    }

    [Fact]
    public async Task Quiet_hours_send_nothing()
    {
        // 22:30 IST = 17:00 UTC; the job returns before it touches the database (db is null here).
        var job = Job(ListingKinds.Stay, new DateTime(2026, 11, 25, 17, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, await job.RunAsync(CancellationToken.None));
    }

    [Fact]
    public void One_push_per_booking_and_the_same_booking_keeps_its_key()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var lines = $$"""[{"id":"{{a}}","label":"Neha","detail":"2 guests","from":null},{"id":"{{b}}","label":"Ravi","detail":null,"from":null}]""";
        var job = Job(ListingKinds.Stay);
        var first = job.Plan([Row(lines), Row(null)], Tomorrow);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(p => p.Key).Distinct().Count());

        var again = job.Plan([Row(lines)], Tomorrow);
        Assert.Equal(first.Select(p => p.Key), again.Select(p => p.Key));
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", OwnerNotifier.PushSql);
    }

    [Fact]
    public void Late_pass_at_21_reuses_the_send_once_key_of_the_evening_pass()
    {
        var options = Options.Create(new OwnerFeatureOptions());
        OwnerEveAlertJob stay = new StayEveLateAlertJob(null!, null!, options, TimeProvider.System, NullLogger<StayEveAlertJob>.Instance);
        OwnerEveAlertJob taxi = new TaxiEveLateAlertJob(null!, null!, options, TimeProvider.System, NullLogger<TaxiEveAlertJob>.Instance);
        Assert.Equal((new TimeOnly(21, 0), "owner.eve.stay.late"), (stay.DailyAtIst, stay.Name));
        Assert.Equal((new TimeOnly(21, 0), "owner.eve.taxi.late"), (taxi.DailyAtIst, taxi.Name));
        Assert.True(OwnerEveAlertJob.LateHour < OwnerEveAlertJob.QuietFrom);

        var entry = Guid.NewGuid();
        var lines = $$"""[{"id":"{{entry}}","label":"Neha","detail":null,"from":"Kathgodam hub"}]""";
        Assert.Equal(Job(ListingKinds.Stay).Plan([Row(lines)], Tomorrow).Single().Key, stay.Plan([Row(lines)], Tomorrow).Single().Key);
        Assert.Equal(Job(ListingKinds.Taxi).Plan([Row(lines)], Tomorrow).Single().Key, taxi.Plan([Row(lines)], Tomorrow).Single().Key);
        Assert.Equal(Job(ListingKinds.Stay).BatchSql, stay.BatchSql);
    }

    [Fact]
    public void Bad_lines_are_skipped()
    {
        Assert.Empty(OwnerEveAlertJob.ParseLines("not json"));
        Assert.Empty(OwnerEveAlertJob.ParseLines("""[{"id":"x"}]"""));
        Assert.Empty(OwnerEveAlertJob.ParseLines(null));
    }

    private static OwnerEveAlertJob.ListingRow Row(string? lines) => new() { ListingId = Guid.NewGuid(), UserId = User, Lines = lines };
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupB;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Retention.GroupB;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. Rows are seeded 400 days old so every default clock has passed.
public class TripRetentionIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now.AddDays(-400);
    private static readonly DateOnly Today = IstTime.TodayIst(Now);

    private async Task RunAsync(params string[] only)
    {
        foreach (var rule in new GroupBRetentionRules().Create().Where(r => only.Length == 0 || only.Contains(r.Name)))
        {
            await using var ctx = CreateContext();
            var days = rule.DefaultDays;
            var rc = new RetentionContext(ctx, Now, Now.AddDays(-days), Today, Today.AddDays(-days), days);
            while (await rule.DeleteBatchAsync(rc, 1000, CancellationToken.None) > 0) { }
        }
    }

    private async Task<Guid> UserAsync()
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = "Retention", IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CreatedAt = Old, UpdatedAt = Old,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> TripAsync(Guid owner, string status, DateOnly? end)
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        ctx.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = owner, CityKey = 210000001, CityName = "Nainital", Title = "T", Status = status, PlanJson = "{}",
            StartDate = end?.AddDays(-2), EndDate = end, CreatedAt = Old, UpdatedAt = Old,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> EnquiryAsync(Guid? trip, Guid user, string status, string? failReason = null, Action<TripEnquiry>? tweak = null)
    {
        await using var ctx = CreateContext();
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TripId = trip, TravellerUserId = user, OwnerUserId = user, Kind = "Stay", ListingId = Guid.NewGuid(),
            ListingName = "Hill View", Status = status, FailReason = failReason, StepKey = "stay:" + Random.Shared.Next(1, 999),
            ExpiresAt = Old, CreatedAt = Old, UpdatedAt = Old,
        };
        tweak?.Invoke(e);
        ctx.TripEnquiries.Add(e);
        await ctx.SaveChangesAsync();
        return e.Id;
    }

    private async Task<Guid> OfferAsync(Guid enquiry, string status)
    {
        await using var ctx = CreateContext();
        var o = new TripOffer
        {
            Id = Guid.NewGuid(), TripEnquiryId = enquiry, SenderUserId = Guid.NewGuid(), Status = status, LinesJson = "[]", CreatedAt = Old,
            RespondedAt = Old,
        };
        ctx.TripOffers.Add(o);
        await ctx.SaveChangesAsync();
        return o.Id;
    }

    private async Task<bool> EnquiryExistsAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.TripEnquiries.AnyAsync(e => e.Id == id);
    }

    private async Task<bool> OfferExistsAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.TripOffers.AnyAsync(o => o.Id == id);
    }

    [IntegrationTheory]
    [InlineData("Expired", null)]
    [InlineData("Declined", null)]
    [InlineData("NotAvailable", null)]
    [InlineData("CancelledByTraveller", null)]
    [InlineData("CancelledByOwner", null)]
    [InlineData("Closed", "Withdrawn")]
    [InlineData("Failed", "DriverDenied")]
    [InlineData("Failed", "HoldExpired")]
    public async Task Closed_old_enquiry_and_its_offers_go(string status, string? reason)
    {
        var user = await UserAsync();
        var trip = await TripAsync(user, "Completed", Today.AddDays(-300));
        var enquiry = await EnquiryAsync(trip, user, status, reason);
        var offer = await OfferAsync(enquiry, "Expired");

        await RunAsync();

        Assert.False(await EnquiryExistsAsync(enquiry));
        Assert.False(await OfferExistsAsync(offer));
    }

    [IntegrationTheory]
    [InlineData("Asked")]
    [InlineData("Offered")]
    [InlineData("Countered")]
    [InlineData("ConfirmedByBoth")]
    [InlineData("FareSent")]
    [InlineData("BookedByTraveller")]
    public async Task Open_enquiries_and_their_offers_survive(string status)
    {
        var user = await UserAsync();
        var enquiry = await EnquiryAsync(null, user, status);
        var offer = await OfferAsync(enquiry, "Superseded");

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(enquiry));
        Assert.True(await OfferExistsAsync(offer));
    }

    [IntegrationFact]
    public async Task Failed_with_a_problem_reason_survives()
    {
        var user = await UserAsync();
        var enquiry = await EnquiryAsync(null, user, "Failed", "ProblemDriverNoShow");

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(enquiry));
    }

    [IntegrationFact]
    public async Task Closed_enquiry_that_ever_reached_a_booking_survives()
    {
        var user = await UserAsync();
        var confirmed = await EnquiryAsync(null, user, "Closed", "Withdrawn", e => e.ConfirmedByTravellerAt = Old);
        var confirmedOwner = await EnquiryAsync(null, user, "Closed", "Withdrawn", e => e.ConfirmedByOwnerAt = Old);
        var booked = await EnquiryAsync(null, user, "Closed", "Withdrawn", e => e.BookedByTravellerAt = Old);
        var advance = await EnquiryAsync(null, user, "CancelledByTraveller", null, e => { e.AdvanceStatus = "Received"; e.AdvanceReceivedAt = Old; });
        var advancePending = await EnquiryAsync(null, user, "Expired", null, e => e.AdvanceStatus = "Pending");

        await RunAsync();

        foreach (var id in new[] { confirmed, confirmedOwner, booked, advance, advancePending })
            Assert.True(await EnquiryExistsAsync(id));
    }

    [IntegrationFact]
    public async Task Accepted_offer_keeps_the_enquiry_and_the_offer()
    {
        var user = await UserAsync();
        var enquiry = await EnquiryAsync(null, user, "Closed", "Withdrawn");
        var accepted = await OfferAsync(enquiry, "Accepted");
        await using (var ctx = CreateContext())
            await ctx.TripEnquiries.Where(e => e.Id == enquiry).ExecuteUpdateAsync(s => s.SetProperty(e => e.AcceptedOfferId, accepted));

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(enquiry));
        Assert.True(await OfferExistsAsync(accepted));
    }

    [IntegrationFact]
    public async Task Pending_offer_survives_even_if_its_enquiry_is_closed_and_old()
    {
        var user = await UserAsync();
        var enquiry = await EnquiryAsync(null, user, "Expired");
        var pending = await OfferAsync(enquiry, "Pending");
        var spent = await OfferAsync(enquiry, "Expired");

        await RunAsync("TripOffers");

        Assert.True(await OfferExistsAsync(pending));
        Assert.False(await OfferExistsAsync(spent));
    }

    [IntegrationFact]
    public async Task Offers_of_a_protected_enquiry_stay()
    {
        var user = await UserAsync();
        var booked = await EnquiryAsync(null, user, "Closed", "Withdrawn", e => e.BookedByTravellerAt = Old);
        var offer = await OfferAsync(booked, "Superseded");

        await RunAsync("TripOffers");

        Assert.True(await OfferExistsAsync(offer));
    }

    [IntegrationFact]
    public async Task Enquiry_with_recent_activity_survives()
    {
        var user = await UserAsync();
        var recent = await EnquiryAsync(null, user, "Expired", null, e => e.UpdatedAt = Now.AddDays(-10));
        var offer = await OfferAsync(recent, "Expired");

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(recent));
        Assert.True(await OfferExistsAsync(offer));
    }

    [IntegrationFact]
    public async Task Running_trip_keeps_its_closed_enquiries_and_a_finished_trip_does_not()
    {
        var user = await UserAsync();
        var running = await TripAsync(user, "Planned", Today.AddDays(5));
        var noEnd = await TripAsync(user, "Planned", null);
        var ended = await TripAsync(user, "Planned", Today.AddDays(-3));
        var keepRunning = await EnquiryAsync(running, user, "Expired");
        var keepNoEnd = await EnquiryAsync(noEnd, user, "Expired");
        var goEnded = await EnquiryAsync(ended, user, "Expired");

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(keepRunning));
        Assert.True(await EnquiryExistsAsync(keepNoEnd));
        Assert.False(await EnquiryExistsAsync(goEnded));
    }

    [IntegrationFact]
    public async Task Open_problem_log_and_report_keep_the_enquiry()
    {
        var user = await UserAsync();
        var trip = await TripAsync(user, "Completed", Today.AddDays(-300));
        var withProblem = await EnquiryAsync(trip, user, "Expired");
        var withPending = await EnquiryAsync(trip, user, "Expired");
        var withRecentlyResolved = await EnquiryAsync(trip, user, "Expired");
        var withOldResolved = await EnquiryAsync(trip, user, "Expired");
        await using (var ctx = CreateContext())
        {
            var reasonId = await ctx.ReportReasons.Select(r => r.Id).FirstAsync();
            ctx.TripProblemLogs.Add(new TripProblemLog { Id = Guid.NewGuid(), TripId = trip, TripEnquiryId = withProblem, Day = 1, Kind = "Other", CreatedAt = Old });
            ctx.ListingReports.AddRange(
                Report(user, reasonId, withPending, "Pending", null),
                Report(user, reasonId, withRecentlyResolved, "Resolved", Now.AddDays(-5)),
                Report(user, reasonId, withOldResolved, "Resolved", Old));
            await ctx.SaveChangesAsync();
        }

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(withProblem));
        Assert.True(await EnquiryExistsAsync(withPending));
        Assert.True(await EnquiryExistsAsync(withRecentlyResolved));
        Assert.False(await EnquiryExistsAsync(withOldResolved));
    }

    private static ListingReport Report(Guid user, Guid reason, Guid enquiry, string status, DateTime? resolvedAt) => new()
    {
        Id = Guid.NewGuid(), ListingId = enquiry, ListingType = "TripEnquiry", ReporterUserId = user, ReportedUserId = user,
        ReasonId = reason, Details = "x", Status = status, ResolvedAt = resolvedAt, CreatedAt = Old, TripEnquiryId = enquiry,
    };

    [IntegrationFact]
    public async Task Closed_enquiry_is_kept_while_another_open_one_shares_its_trip_and_step()
    {
        var user = await UserAsync();
        var trip = await TripAsync(user, "Completed", Today.AddDays(-300));
        var closed = await EnquiryAsync(trip, user, "Expired", null, e => e.StepKey = "stay:7");
        var open = await EnquiryAsync(trip, user, "Asked", null, e => e.StepKey = "stay:7");

        await RunAsync();

        Assert.True(await EnquiryExistsAsync(closed));
        Assert.True(await EnquiryExistsAsync(open));
    }

    [IntegrationFact]
    public async Task Orphan_rule_uses_180_days_and_keeps_protected_orphans()
    {
        var user = await UserAsync();
        var young = await EnquiryAsync(null, user, "Closed", "TripDeleted", e => e.UpdatedAt = Now.AddDays(-120));
        var old = await EnquiryAsync(null, user, "Closed", "TripDeleted", e => e.UpdatedAt = Now.AddDays(-200));
        var orphanBooked = await EnquiryAsync(null, user, "Closed", "TripDeleted", e => { e.UpdatedAt = Now.AddDays(-200); e.BookedByTravellerAt = Old; });

        await RunAsync("TripEnquiryOrphans");

        Assert.True(await EnquiryExistsAsync(young));
        Assert.False(await EnquiryExistsAsync(old));
        Assert.True(await EnquiryExistsAsync(orphanBooked));
    }

    [IntegrationFact]
    public async Task Outbox_only_published_rows_older_than_three_days_go()
    {
        var key = Guid.NewGuid().ToString("N");
        await using (var ctx = CreateContext())
        {
            ctx.OutboxMessages.AddRange(
                Outbox(key + "a", Now.AddDays(-10), Now.AddDays(-10)),
                Outbox(key + "b", Now.AddDays(-1), Now.AddDays(-1)),
                Outbox(key + "c", Old, null));
            await ctx.SaveChangesAsync();
        }

        await RunAsync("OutboxMessages");

        await using var check = CreateContext();
        var left = await check.OutboxMessages.Where(o => o.Key.StartsWith(key)).Select(o => o.Key).ToListAsync();
        Assert.DoesNotContain(key + "a", left);
        Assert.Contains(key + "b", left);
        Assert.Contains(key + "c", left);
    }

    private static OutboxMessage Outbox(string key, DateTime created, DateTime? published) => new()
    {
        Key = key, Exchange = "rnb.push", RoutingKey = "send", Payload = "{}", CreatedAt = created, NextAttemptAt = created, PublishedAt = published,
    };

    [IntegrationFact]
    public async Task Reminder_runs_older_than_thirty_days_go_but_not_recent_ones()
    {
        var oldDate = Today.AddDays(-400 - Random.Shared.Next(1, 3000));
        var recent = Today.AddDays(-2);
        await using (var ctx = CreateContext())
        {
            ctx.TripReminderRuns.Add(new TripReminderRun { RunDate = oldDate, StartedAt = Old, FinishedAt = Old, Sent = 1 });
            if (!await ctx.TripReminderRuns.AnyAsync(r => r.RunDate == recent))
                ctx.TripReminderRuns.Add(new TripReminderRun { RunDate = recent, StartedAt = Now, Sent = 0 });
            await ctx.SaveChangesAsync();
        }

        await RunAsync("TripReminderRuns");

        await using var check = CreateContext();
        Assert.False(await check.TripReminderRuns.AnyAsync(r => r.RunDate == oldDate));
        Assert.True(await check.TripReminderRuns.AnyAsync(r => r.RunDate == recent));
    }

    [IntegrationFact]
    public async Task Shortlist_Active_and_running_trip_rows_survive_closed_rows_of_finished_trips_go()
    {
        var user = await UserAsync();
        var finished = await TripAsync(user, "Completed", Today.AddDays(-300));
        var running = await TripAsync(user, "Planned", Today.AddDays(5));
        var active = Item(finished, user, "Active", null, null);
        var closedFinished = Item(finished, user, "Closed", Old, null);
        var bookedFinished = Item(finished, user, "Booked", Old, null);
        var sentFinished = Item(finished, user, "Sent", null, Old);
        var closedRunning = Item(running, user, "Closed", Old, null);
        await using (var ctx = CreateContext())
        {
            ctx.TripShortlistItems.AddRange(active, closedFinished, bookedFinished, sentFinished, closedRunning);
            await ctx.SaveChangesAsync();
        }

        await RunAsync("TripShortlistItems");

        await using var check = CreateContext();
        var left = await check.TripShortlistItems.Select(i => i.Id).ToListAsync();
        Assert.Contains(active.Id, left);
        Assert.Contains(closedRunning.Id, left);
        Assert.DoesNotContain(closedFinished.Id, left);
        Assert.DoesNotContain(bookedFinished.Id, left);
        Assert.DoesNotContain(sentFinished.Id, left);
    }

    private static TripShortlistItem Item(Guid trip, Guid user, string state, DateTime? closedAt, DateTime? closesAt) => new()
    {
        Id = Guid.NewGuid(), TripId = trip, TravellerUserId = user, StepKey = "stay:1", Kind = "Stay", ListingId = Guid.NewGuid(), ListingName = "Hill View",
        AddedAt = Old, ClosesAt = closesAt ?? Now.AddDays(1), State = state, ClosedReason = state == "Closed" ? "NoReply" : null, ClosedAt = closedAt,
    };

    [IntegrationFact]
    public async Task Running_the_rules_twice_is_harmless()
    {
        var user = await UserAsync();
        var enquiry = await EnquiryAsync(null, user, "Expired");

        await RunAsync();
        await RunAsync();

        Assert.False(await EnquiryExistsAsync(enquiry));
    }
}

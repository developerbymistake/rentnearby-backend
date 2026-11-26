using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Taxis;

namespace RentNearBy.Tests.TaxiAvailability;

// Audit L1: a legacy taxi lead entering or leaving ConfirmedByBoth (cancel, withdraw, trip delete, timer, listing removal) or a
// confirmed lead changing its days bumps taxis:avail:gen once, after commit; a rollback never bumps.
public class LegacyLeadAvailGenTests
{
    private const string Confirmed = TripEnquiryStatuses.ConfirmedByBoth;

    private static bool Moves(EntityState state, string kind, string? status, string? original, params string[] modified)
        => TaxiAvailabilityBumpInterceptor.MovesAvailability(state, kind, status, original, p => modified.Contains(p));

    [Theory]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner)]
    [InlineData(TripEnquiryStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Failed)]
    public void A_confirmed_taxi_lead_leaving_moves_availability(string to)
        => Assert.True(Moves(EntityState.Modified, TripEnquiryKinds.Taxi, to, Confirmed, nameof(TripEnquiry.Status)));

    [Fact]
    public void A_taxi_lead_becoming_confirmed_moves_availability()
        => Assert.True(Moves(EntityState.Modified, TripEnquiryKinds.Taxi, Confirmed, TripEnquiryStatuses.BookedByTraveller, nameof(TripEnquiry.Status)));

    [Theory]
    [InlineData(nameof(TripEnquiry.CheckIn), true)]
    [InlineData(nameof(TripEnquiry.CheckOut), true)]
    [InlineData(nameof(TripEnquiry.DaysJson), true)]
    [InlineData(nameof(TripEnquiry.UpdatedAt), false)]
    [InlineData(nameof(TripEnquiry.TripId), false)]
    public void A_confirmed_taxi_lead_moves_availability_only_when_its_days_change(string column, bool expected)
        => Assert.Equal(expected, Moves(EntityState.Modified, TripEnquiryKinds.Taxi, Confirmed, Confirmed, column));

    [Theory]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, TripEnquiryStatuses.CancelledByTraveller)]
    [InlineData(TripEnquiryStatuses.FareSent, TripEnquiryStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Asked, TripEnquiryStatuses.Closed)]
    public void A_taxi_lead_that_never_was_confirmed_does_not_move_availability(string from, string to)
        => Assert.False(Moves(EntityState.Modified, TripEnquiryKinds.Taxi, to, from, nameof(TripEnquiry.Status), nameof(TripEnquiry.CheckIn)));

    [Fact]
    public void A_stay_enquiry_never_moves_taxi_availability()
        => Assert.False(Moves(EntityState.Modified, TripEnquiryKinds.Stay, TripEnquiryStatuses.CancelledByOwner, Confirmed, nameof(TripEnquiry.Status)));

    [Fact]
    public void Added_and_deleted_rows_move_availability_only_when_confirmed()
    {
        Assert.True(Moves(EntityState.Added, TripEnquiryKinds.Taxi, Confirmed, null));
        Assert.False(Moves(EntityState.Added, TripEnquiryKinds.Taxi, TripEnquiryStatuses.Asked, null));
        Assert.True(Moves(EntityState.Deleted, TripEnquiryKinds.Taxi, Confirmed, Confirmed));
        Assert.False(Moves(EntityState.Deleted, TripEnquiryKinds.Taxi, TripEnquiryStatuses.Closed, TripEnquiryStatuses.Closed));
        Assert.False(Moves(EntityState.Unchanged, TripEnquiryKinds.Taxi, Confirmed, Confirmed));
    }

    [Fact]
    public async Task The_app_context_carries_the_bump_interceptor_and_its_commit_watcher()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        await using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var interceptors = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors!.ToList();
        var bump = Assert.Single(interceptors.OfType<TaxiAvailabilityBumpInterceptor>());
        Assert.Contains((IInterceptor)bump.Commits, interceptors);
        Assert.Single(interceptors.OfType<OwnerCalendarSyncInterceptor>());
    }

    [Fact]
    public void Cancelling_a_tracked_confirmed_lead_is_seen_without_a_detect_call()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Taxi, Confirmed);
        e.Status = TripEnquiryStatuses.CancelledByTraveller;
        Assert.True(TaxiAvailabilityBumpInterceptor.HasMove(db));
    }

    [Fact]
    public void Closing_a_tracked_lead_that_never_was_confirmed_is_not_a_move()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Taxi, TripEnquiryStatuses.Asked);
        e.Status = TripEnquiryStatuses.Closed;
        Assert.False(TaxiAvailabilityBumpInterceptor.HasMove(db));
    }

    [Fact]
    public void Changing_only_the_days_of_a_tracked_confirmed_lead_is_a_move()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Taxi, Confirmed);
        e.CheckOut = e.CheckOut!.Value.AddDays(1);
        Assert.True(TaxiAvailabilityBumpInterceptor.HasMove(db));
    }

    [Fact]
    public void An_untouched_tracked_confirmed_lead_is_not_a_move()
    {
        using var db = TrackerContext.Create();
        TrackerContext.Attach(db, TripEnquiryKinds.Taxi, Confirmed);
        Assert.False(TaxiAvailabilityBumpInterceptor.HasMove(db));
    }
}

// The calendar hook reads the same tracker in SavingChanges, before EF detects changes; these never open a connection.
public class OwnerCalendarTrackerScanTests
{
    [Fact]
    public void Cancelling_a_tracked_confirmed_enquiry_is_watched_without_a_detect_call()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Taxi, TripEnquiryStatuses.ConfirmedByBoth);
        e.Status = TripEnquiryStatuses.CancelledByOwner;
        Assert.Same(e, Assert.Single(OwnerCalendarSyncInterceptor.WatchedEnquiries(db)!));
    }

    [Fact]
    public void Confirming_a_tracked_stay_is_watched()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Stay, TripEnquiryStatuses.Offered);
        e.Status = TripEnquiryStatuses.ConfirmedByBoth;
        Assert.Same(e, Assert.Single(OwnerCalendarSyncInterceptor.WatchedEnquiries(db)!));
    }

    [Fact]
    public void Closing_a_tracked_enquiry_that_never_was_confirmed_is_not_watched()
    {
        using var db = TrackerContext.Create();
        var e = TrackerContext.Attach(db, TripEnquiryKinds.Taxi, TripEnquiryStatuses.Asked);
        e.Status = TripEnquiryStatuses.Closed;
        Assert.Null(OwnerCalendarSyncInterceptor.WatchedEnquiries(db));
    }
}

internal static class TrackerContext
{
    public static ApplicationDbContext Create() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=test;Username=test;Password=test", o => o.UseNetTopologySuite())
        .Options);

    public static TripEnquiry Attach(ApplicationDbContext db, string kind, string status)
    {
        var today = new DateOnly(2026, 12, 1);
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), Kind = kind, Status = status, ListingId = Guid.NewGuid(), ListingName = "Hill Taxi",
            TravellerUserId = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), CheckIn = today, CheckOut = today.AddDays(2),
        };
        db.Attach(e);
        return e;
    }
}

// Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on this machine.
public class LegacyLeadAvailGenIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private ApplicationDbContext HookedContext(out List<string> keys)
    {
        var bump = new TaxiAvailabilityBumpInterceptor(TaxiPhotoIntegrationTests.RecordingRedis.Create(out keys));
        return new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Db.ConnectionString!, o => o.UseNetTopologySuite())
            .AddInterceptors(new OwnerCalendarSyncInterceptor(new OwnerCalendarSync()), bump, bump.Commits)
            .Options);
    }

    private static int Bumps(List<string> keys)
    {
        lock (keys) return keys.Count(k => k == ListingCacheKeys.TaxiAvailGen);
    }

    private async Task<Guid> LeadAsync(string status)
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c);
        var traveller = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Neha");
        var now = DateTime.UtcNow;
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TravellerUserId = traveller, OwnerUserId = driver.UserId, Kind = TripEnquiryKinds.Taxi, ListingId = driver.ListingId,
            ListingName = "Hill Taxi", CheckIn = TodayIst.AddDays(5), CheckOut = TodayIst.AddDays(7), Status = status,
            ExpiresAt = now.AddDays(2), CreatedAt = now, UpdatedAt = now,
        };
        c.Set<TripEnquiry>().Add(e);
        await c.SaveChangesAsync();
        return e.Id;
    }

    private static async Task SetStatusAsync(ApplicationDbContext c, Guid id, string status)
    {
        var e = await c.Set<TripEnquiry>().SingleAsync(x => x.Id == id);
        e.Status = status;
        e.UpdatedAt = DateTime.UtcNow;
        await c.SaveChangesAsync();
    }

    [IntegrationFact]
    public async Task Cancelling_a_confirmed_lead_bumps_once()
    {
        var id = await LeadAsync(TripEnquiryStatuses.ConfirmedByBoth);
        await using var c = HookedContext(out var keys);
        await SetStatusAsync(c, id, TripEnquiryStatuses.CancelledByTraveller);
        Assert.Equal(1, Bumps(keys));
    }

    [IntegrationFact]
    public async Task Inside_a_transaction_the_bump_waits_for_the_commit()
    {
        var id = await LeadAsync(TripEnquiryStatuses.ConfirmedByBoth);
        await using var c = HookedContext(out var keys);
        await using (var tx = await c.Database.BeginTransactionAsync())
        {
            await SetStatusAsync(c, id, TripEnquiryStatuses.CancelledByOwner);
            Assert.Equal(0, Bumps(keys));
            await tx.CommitAsync();
        }
        Assert.Equal(1, Bumps(keys));
    }

    [IntegrationFact]
    public async Task A_rolled_back_cancel_never_bumps()
    {
        var id = await LeadAsync(TripEnquiryStatuses.ConfirmedByBoth);
        await using var c = HookedContext(out var keys);
        await using (var tx = await c.Database.BeginTransactionAsync())
        {
            await SetStatusAsync(c, id, TripEnquiryStatuses.CancelledByOwner);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, Bumps(keys));
    }

    [IntegrationFact]
    public async Task Closing_a_lead_that_was_never_confirmed_does_not_bump()
    {
        var id = await LeadAsync(TripEnquiryStatuses.BookedByTraveller);
        await using var c = HookedContext(out var keys);
        await SetStatusAsync(c, id, TripEnquiryStatuses.Closed);
        Assert.Equal(0, Bumps(keys));
    }
}

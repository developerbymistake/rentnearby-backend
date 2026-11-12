using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. A host with no device token still gets the reminder in the inbox.
public class TripReminderInboxIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime RunAt = TripReminderRules.MorningDueUtc(IstTime.TodayIst(DateTime.UtcNow)).AddHours(2);
    private static readonly DateOnly Today = IstTime.TodayIst(RunAt);

    private static async Task<Guid> UserAsync(ApplicationDbContext ctx, string name)
    {
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = name, IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    [IntegrationFact]
    public async Task Host_without_a_push_token_gets_one_inbox_row_and_a_second_run_adds_none()
    {
        Guid host;
        await using (var ctx = CreateContext())
        {
            var traveller = await UserAsync(ctx, "Asha");
            host = await UserAsync(ctx, "Host");
            var trip = Guid.NewGuid();
            ctx.Trips.Add(new Trip
            {
                Id = trip, OwnerUserId = traveller, CityKey = 210000001, CityName = "Nainital", Title = "Nainital", Status = TripStatuses.Planned,
                PlanJson = "{}", StartDate = Today.AddDays(30), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            ctx.TripEnquiries.Add(new TripEnquiry
            {
                Id = Guid.NewGuid(), TripId = trip, TravellerUserId = traveller, OwnerUserId = host, Kind = "Stay", ListingId = Guid.NewGuid(),
                ListingName = "Hill View", Status = "Asked", StepKey = "stay:1", ExpiresAt = RunAt.AddDays(1),
                CreatedAt = RunAt.AddDays(-1), UpdatedAt = RunAt.AddDays(-1),
            });
            await ctx.SaveChangesAsync();
        }

        for (var i = 0; i < 2; i++)
        {
            await using var ctx = CreateContext();
            await new TripReminderRunner(NullLogger.Instance).RunAsync(ctx, RunAt, CancellationToken.None);
        }

        await using var check = CreateContext();
        Assert.Empty(await check.DeviceTokens.Where(d => d.UserId == host).ToListAsync());
        var rows = await check.NotificationEvents.AsNoTracking().Where(n => n.TargetUserId == host).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(NotificationTypes.TripReminder, rows[0].Type);
    }
}

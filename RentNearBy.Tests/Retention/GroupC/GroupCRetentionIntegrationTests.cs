using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupC;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Retention.GroupC;

// Needs PostgreSQL (RENTNEARBY_TEST_DB); the integration facts are skipped without it.
public class GroupCRetentionIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static async Task RunAsync(ApplicationDbContext ctx, string name)
    {
        var rule = new GroupCRetentionRules().Create().Single(r => r.Name == name);
        var today = IstTime.TodayIst(Now);
        var rc = new RetentionContext(ctx, Now, Now.AddDays(-90), today, today.AddDays(-90), 90);
        while (await rule.DeleteBatchAsync(rc, 1000, default) > 0) { }
    }

    private static async Task<Guid> UserAsync(ApplicationDbContext ctx)
    {
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = "u", IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CreatedAt = Now.AddDays(-400), UpdatedAt = Now.AddDays(-400),
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    [IntegrationFact]
    public async Task Sessions_live_survives_expired_and_revoked_go()
    {
        await using var ctx = CreateContext();
        var u = await UserAsync(ctx);
        Session S(DateTime exp, bool active, DateTime? loggedOut = null) =>
            new() { Id = Guid.NewGuid(), UserId = u, CreatedAt = Now.AddDays(-500), ExpiresAt = exp, IsActive = active, LoggedOutAt = loggedOut };
        var live = S(Now.AddDays(10), true);
        var expiredRecent = S(Now.AddDays(-5), true);
        var expiredOld = S(Now.AddDays(-120), true);
        var revokedOld = S(Now.AddDays(50), false, Now.AddDays(-100));
        var revokedRecent = S(Now.AddDays(50), false, Now.AddDays(-10));
        ctx.Sessions.AddRange(live, expiredRecent, expiredOld, revokedOld, revokedRecent);
        await ctx.SaveChangesAsync();

        await RunAsync(ctx, "Sessions");

        await using var check = CreateContext();
        var ids = await check.Sessions.Where(s => s.UserId == u).Select(s => s.Id).ToListAsync();
        Assert.Contains(live.Id, ids);
        Assert.Contains(expiredRecent.Id, ids);
        Assert.Contains(revokedRecent.Id, ids);
        Assert.DoesNotContain(expiredOld.Id, ids);
        Assert.DoesNotContain(revokedOld.Id, ids);
    }

    [IntegrationFact]
    public async Task AdminSessions_live_survives_expired_and_revoked_go()
    {
        await using var ctx = CreateContext();
        var admin = new Admin { Id = Guid.NewGuid(), Email = $"a{Guid.NewGuid():N}@x.test", PasswordHash = "x", PhoneNumber = "9000000000" };
        ctx.Admins.Add(admin);
        await ctx.SaveChangesAsync();
        AdminSession S(DateTime exp, bool active, DateTime? loggedOut = null) =>
            new() { Id = Guid.NewGuid(), AdminId = admin.Id, CreatedAt = Now.AddDays(-500), ExpiresAt = exp, IsActive = active, LoggedOutAt = loggedOut };
        var live = S(Now.AddDays(3), true);
        ctx.AdminSessions.AddRange(live, S(Now.AddDays(-100), true), S(Now.AddDays(20), false, Now.AddDays(-95)));
        await ctx.SaveChangesAsync();

        await RunAsync(ctx, "AdminSessions");

        await using var check = CreateContext();
        Assert.Equal([live.Id], await check.AdminSessions.Where(s => s.AdminId == admin.Id).Select(s => s.Id).ToListAsync());
    }

    [IntegrationFact]
    public async Task StayJobs_active_survives_old_closed_goes_recent_closed_stays()
    {
        await using var ctx = CreateContext();
        var u = await UserAsync(ctx);
        StayJob J(string status, DateTime? closedAt, DateTime? pushed) => new()
        {
            Id = Guid.NewGuid(), StayListingId = Guid.NewGuid(), UserId = u, Title = "t", Description = "d", Status = status,
            ContactPhone = "9000000000", WhatsAppNumber = "9000000000", CreatedAt = Now.AddDays(-200), UpdatedAt = closedAt ?? Now.AddDays(-200),
            ClosedAt = closedAt, SeekerPushedAt = pushed,
        };
        var active = J("Active", null, Now.AddDays(-199));
        var closedOld = J("Closed", Now.AddDays(-120), Now.AddDays(-199));
        var closedRecent = J("Closed", Now.AddDays(-5), Now.AddDays(-199));
        ctx.StayJobs.AddRange(active, closedOld, closedRecent);
        await ctx.SaveChangesAsync();

        await RunAsync(ctx, "StayJobs");

        await using var check = CreateContext();
        var ids = await check.StayJobs.Where(j => j.UserId == u).Select(j => j.Id).ToListAsync();
        Assert.Contains(active.Id, ids);
        Assert.Contains(closedRecent.Id, ids);
        Assert.DoesNotContain(closedOld.Id, ids);
    }

    [IntegrationFact]
    public async Task BannerDismissals_removes_only_old_dismissals_of_inactive_banners()
    {
        await using var ctx = CreateContext();
        var u = await UserAsync(ctx);
        var district = new District { Id = Guid.NewGuid(), Name = "D" + Guid.NewGuid().ToString("N")[..6], StateName = "S", CreatedAt = Now };
        ctx.Districts.Add(district);
        DistrictBanner B(bool active) => new() { Id = Guid.NewGuid(), DistrictId = district.Id, ImageUrl = "u", ImageFilePath = "p", IsActive = active, CreatedAt = Now.AddDays(-300) };
        var inactive = B(false);
        var active = B(true);
        ctx.DistrictBanners.AddRange(inactive, active);
        await ctx.SaveChangesAsync();
        ctx.BannerDismissals.AddRange(
            new BannerDismissal { UserId = u, BannerId = inactive.Id, DismissedAt = Now.AddDays(-200) },
            new BannerDismissal { UserId = u, BannerId = active.Id, DismissedAt = Now.AddDays(-200) });
        await ctx.SaveChangesAsync();

        await RunAsync(ctx, "BannerDismissals");

        await using var check = CreateContext();
        Assert.Equal([active.Id], await check.BannerDismissals.Where(d => d.UserId == u).Select(d => d.BannerId).ToListAsync());
    }

    [Fact]
    public void BannerDismissals_rule_is_off_by_default()
    {
        Assert.False(new GroupCRetentionRules().Create().Single(r => r.Name == "BannerDismissals").DefaultEnabled);
    }
}

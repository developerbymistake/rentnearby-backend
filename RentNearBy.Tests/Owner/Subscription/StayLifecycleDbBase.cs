using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Shared database helpers of the Stay lifecycle integration tests. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on the
// build machine. The plan state of an owner is a real UserPlans row: the limits come from a real PlanEntitlementService, the plan
// effect from the real PlanLimitsReconcile (single owner path as the grant / expiry hooks run it, batch path as the daily pass).
public abstract class StayLifecycleDbBase(TestDatabase db) : IntegrationTestBase(db)
{
    protected sealed class RecordingNotifier : IOwnerNotifier
    {
        public List<NoticeUpsert> Stored { get; } = new();
        public List<OwnerPush> Pushed { get; } = new();
        public List<(string Kind, Guid Entity, Guid[] Users)> Cleared { get; } = new();
        public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) { Stored.AddRange(notices); return Task.CompletedTask; }
        public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) { Cleared.Add((kind, entityId, [.. userIds])); return Task.CompletedTask; }
        public Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default) { Pushed.AddRange(pushes); return Task.CompletedTask; }
        public void Wake() { }
    }

    protected static PlanLimitsReconcile Reconcile(ApplicationDbContext c, bool paymentOn, RecordingNotifier notifier)
        => new(c, new FixedPlanCatalog(OwnerPlanCatalog.Seeded()), new FixedPaymentFeature(paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off()),
            null!, notifier, NullLogger<PlanLimitsReconcile>.Instance);

    protected static PlanEntitlementService Entitlements(ApplicationDbContext c, bool paymentOn)
        => new(c, new FixedPlanCatalog(OwnerPlanCatalog.Seeded()), new FixedPaymentFeature(paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off()));

    // The grant / expiry hook: owner lock, limits from the owner's real plan rows, the plan effect, commit.
    protected async Task<PlanRowsEffect> ApplyAsync(Guid userId, bool paymentOn)
    {
        await using var c = CreateContext();
        await using var tx = await c.Database.BeginTransactionAsync();
        await c.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
        var limits = await Entitlements(c, paymentOn).GetLimitsAsync(userId, ListingKinds.Stay);
        var effect = await Reconcile(c, paymentOn, new RecordingNotifier()).ApplyForOwnerAsync(userId, ListingKinds.Stay, limits);
        await tx.CommitAsync();
        return effect;
    }

    protected async Task<PlanRowsEffect> ApplyWithAsync(Guid userId, PlanLimits limits)
    {
        await using var c = CreateContext();
        await using var tx = await c.Database.BeginTransactionAsync();
        await c.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
        var effect = await Reconcile(c, limits.PaymentEnabled, new RecordingNotifier()).ApplyForOwnerAsync(userId, ListingKinds.Stay, limits);
        await tx.CommitAsync();
        return effect;
    }

    // The daily pass over every Stay listing of the test database; assert only on your own owner afterwards.
    protected async Task BatchAsync(bool paymentOn, RecordingNotifier notifier)
    {
        await using var c = CreateContext();
        await Reconcile(c, paymentOn, notifier).ReconcileAsync(ListingKinds.Stay);
    }

    protected async Task<Guid> BuyPlusAsync(Guid userId)
    {
        await using var c = CreateContext();
        return await OwnerSeed.ActivePlanAsync(c, userId, ListingKinds.Stay, OwnerPlanCatalog.StayPlusId);
    }

    // endsInDays: null keeps EndsAt (expire by status, hold, refund); a negative number ends the row by the clock.
    protected async Task SetPlanAsync(Guid planRowId, string status, int? endsInDays = null)
    {
        await using var c = CreateContext();
        if (endsInDays is { } days)
        {
            var ends = DateTime.UtcNow.AddDays(days);
            await c.UserPlans.Where(u => u.Id == planRowId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status).SetProperty(u => u.EndsAt, ends));
        }
        else
        {
            await c.UserPlans.Where(u => u.Id == planRowId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
        }
    }

    protected async Task<List<StayRoom>> RoomsOfAsync(Guid stayId)
    {
        await using var c = CreateContext();
        return await c.StayRooms.IgnoreQueryFilters().AsNoTracking().Where(r => r.StayListingId == stayId).OrderBy(r => r.RoomIndex).ToListAsync();
    }

    protected async Task<List<StayPhoto>> PhotosOfAsync(Guid stayId)
    {
        await using var c = CreateContext();
        return await c.StayPhotos.IgnoreQueryFilters().AsNoTracking().Where(p => p.StayListingId == stayId).OrderBy(p => p.PhotoOrder).ToListAsync();
    }

    protected async Task<List<StayJob>> JobsOfAsync(Guid userId)
    {
        await using var c = CreateContext();
        return await c.StayJobs.AsNoTracking().Where(j => j.UserId == userId).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    protected async Task<List<StaySpecialPrice>> RulesOfAsync(Guid stayId)
    {
        await using var c = CreateContext();
        return await c.Set<StaySpecialPrice>().IgnoreQueryFilters().AsNoTracking().Where(p => p.StayListingId == stayId).OrderBy(p => p.CreatedAt).ToListAsync();
    }

    // Jobs need the five lookup rows of the Jobs module; created once per call with unique names.
    protected async Task<List<StayJob>> SeedJobsAsync(OwnerSeed.OwnerRow owner, int count)
    {
        await using var c = CreateContext();
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var department = new JobDepartment { Id = Guid.NewGuid(), Name = $"Kitchen {tag}", SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var role = new JobRole { Id = Guid.NewGuid(), Name = $"Cook {tag}", JobDepartmentId = department.Id, SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var type = new JobType { Id = Guid.NewGuid(), Name = $"Full time {tag}", SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var shift = new JobShiftType { Id = Guid.NewGuid(), Name = $"Day {tag}", SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var salary = new JobSalaryType { Id = Guid.NewGuid(), Name = $"Monthly {tag}", SortOrder = 1, IsActive = true, CreatedAt = now, UpdatedAt = now };
        c.JobDepartments.Add(department);
        c.JobRoles.Add(role);
        c.JobTypes.Add(type);
        c.JobShiftTypes.Add(shift);
        c.JobSalaryTypes.Add(salary);
        var jobs = Enumerable.Range(0, count).Select(i => new StayJob
        {
            Id = Guid.NewGuid(), StayListingId = owner.ListingId, UserId = owner.UserId, Title = $"Cook {i + 1}", Description = "Cook for the stay kitchen.",
            JobDepartmentId = department.Id, JobRoleId = role.Id, JobTypeId = type.Id, JobShiftTypeId = shift.Id, JobSalaryTypeId = salary.Id,
            MinimumExperienceYears = 1, MinSalary = 15000, MaxSalary = 20000, ContactPhone = "9876543210", WhatsAppNumber = "9876543210",
            Status = StayJobStatuses.Active, CreatedAt = now.AddMinutes(-(count - i)), UpdatedAt = now,
        }).ToList();
        c.StayJobs.AddRange(jobs);
        await c.SaveChangesAsync();
        return jobs;
    }

    // Special prices with distinct CreatedAt (the plan keeps the last added), oldest first.
    protected async Task<List<StaySpecialPrice>> SeedRulesAsync(OwnerSeed.OwnerRow owner, int count)
    {
        await using var c = CreateContext();
        var rules = await OwnerSeed.SpecialPricesAsync(c, owner.ListingId, count, Be2Kit.Today.AddDays(5));
        var created = DateTime.UtcNow.AddHours(-count);
        for (var i = 0; i < rules.Count; i++) // test seed only
        {
            var id = rules[i].Id;
            var at = created.AddMinutes(i);
            await c.Set<StaySpecialPrice>().Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, at));
            rules[i].CreatedAt = at;
        }
        return rules;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// Shared pieces of the stay quotation tests: a movable clock, the seeded catalog behind IPlanEntitlementService (the real Compute),
// a configured or unconfigured cipher, a recording image store and service factories over a given context.
public sealed class QuoteClock(DateTime utc) : TimeProvider
{
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(UtcNow);

    public QuoteClock Set(DateTime utc)
    {
        UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return this;
    }

    public QuoteClock Advance(TimeSpan by) => Set(UtcNow + by);
}

// The production maths (PlanEntitlementService.Compute) over the seeded Free 2 / Plus 30 catalog, payment ON or OFF.
public sealed class QuoteEntitlements(bool paymentOn = true) : IPlanEntitlementService
{
    private readonly PlanCatalogSnapshot _snapshot = OwnerPlanCatalog.Seeded();
    private readonly PaymentFeatureState _flag = paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off();

    public Task<PlanLimits> GetLimitsAsync(Guid userId, string featureKey)
        => Task.FromResult(PlanEntitlementService.Compute(_snapshot, _flag, featureKey, null));

    public Task<IReadOnlyDictionary<Guid, PlanLimits>> GetLimitsBatchAsync(IReadOnlyCollection<Guid> userIds, string featureKey)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlanLimits>>(userIds.ToDictionary(id => id, _ => PlanEntitlementService.Compute(_snapshot, _flag, featureKey, null)));

    public Task<PlanLimits> ComputeFromRowAsync(string featureKey, EntitledPlanRow? row)
        => Task.FromResult(PlanEntitlementService.Compute(_snapshot, _flag, featureKey,
            row == null ? null : new PlanEntitlementService.ActiveRow(row.Id, Guid.Empty, row.PlanId, row.Source, row.StartsAt, row.EndsAt)));
}

public sealed class RecordingQuoteImageStore : IQuoteImageStore
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<Guid> PrefixDeleted { get; } = [];
    public Func<Stream, QuoteImageKind, byte[]>? Process { get; set; }
    public bool FailUpload { get; set; }

    public async Task<string> SaveQuoteImageAsync(Stream input, Guid userId, QuoteImageKind kind)
    {
        if (FailUpload) throw new InvalidOperationException("upload failed");
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy);
        copy.Position = 0;
        var path = QuoteImagePaths.NewPublicId(userId, kind);
        Files[path] = Process?.Invoke(copy, kind) ?? copy.ToArray();
        return path;
    }

    // Set to make every read fail as Cloudinary would (HttpRequestException, TaskCanceledException on the download timeout).
    public Exception? ReadFailure { get; set; }

    public Task<byte[]?> ReadQuoteImageAsync(string publicId, CancellationToken ct)
        => ReadFailure != null ? Task.FromException<byte[]?>(ReadFailure) : Task.FromResult(Files.TryGetValue(publicId, out var b) ? b : null);

    public Task DeleteQuoteImageAsync(string publicId)
    {
        Deleted.Add(publicId);
        Files.Remove(publicId);
        return Task.CompletedTask;
    }

    public Task DeleteAllQuoteImagesAsync(Guid userId)
    {
        PrefixDeleted.Add(userId);
        return Task.CompletedTask;
    }
}

public static class QuoteKit
{
    public static readonly string TestKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public static AesGcmIdentityCipher Cipher(bool configured = true) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AesGcmIdentityCipher.ConfigKey] = configured ? TestKey : null,
        }).Build(),
        NullLogger<AesGcmIdentityCipher>.Instance);

    public static StayQuoteProfileService Profile(ApplicationDbContext db, TimeProvider? clock = null, bool cipherOn = true, bool paymentOn = true,
        RecordingOwnerNotifier? notifier = null)
        => new(db, Cipher(cipherOn), new QuoteEntitlements(paymentOn), notifier ?? new RecordingOwnerNotifier(), clock ?? TimeProvider.System,
            NullLogger<StayQuoteProfileService>.Instance);

    public static StayQuoteReserveService Reserve(ApplicationDbContext db, TimeProvider? clock = null, bool paymentOn = true)
        => new(db, new QuoteEntitlements(paymentOn), clock ?? TimeProvider.System, NullLogger<StayQuoteReserveService>.Instance);

    public static StayQuoteImageService Images(ApplicationDbContext db, IQuoteImageStore store, TimeProvider? clock = null)
        => new(db, store, clock ?? TimeProvider.System, NullLogger<StayQuoteImageService>.Instance);

    // An approved stay owner who accepted the current terms (the state every reserve test starts from).
    public static async Task<Guid> ReadyOwnerAsync(TestDatabaseContextFactory factory, string name = "The Banyan Retreat")
    {
        await using var c = factory();
        var owner = await OwnerSeed.StayOwnerAsync(c, name: name);
        c.StayQuoteTermsAcceptances.Add(new StayQuoteTermsAcceptance
        {
            UserId = owner.UserId, Version = QuoteTerms.CurrentVersion, AcceptedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
        return owner.UserId;
    }

    public static async Task PlanAsync(ApplicationDbContext db, Guid userId, string status, DateTime endsAt, Guid? planId = null,
        string source = UserPlanSources.Prepaid)
    {
        var now = DateTime.UtcNow;
        db.UserPlans.Add(new UserPlan
        {
            Id = Guid.NewGuid(), UserId = userId, FeatureKey = ListingKinds.Stay, PlanId = planId ?? OwnerPlanCatalog.StayPlusId,
            Source = source, Status = status, StartsAt = now.AddDays(-40), EndsAt = endsAt, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }
}

public delegate ApplicationDbContext TestDatabaseContextFactory();

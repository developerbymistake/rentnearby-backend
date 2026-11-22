using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.TaxiRequests;
using RentNearBy.Tests.Trips;
using StackExchange.Redis;

namespace RentNearBy.Tests.Stays;

// CONTRACT_BACKEND card photos 7 and 9 (lane BE2): stay photo Replace, delete and whole-stay delete remove the Cloudinary file.
public class StayPhotoReplaceTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    [Fact]
    public void Replace_route_is_photo_limited()
        => Assert.Contains(
            "group.MapPut(\"/me/photos/{photoId:guid}\", StayHandlers.ReplacePhoto).RequireAuthorization(\"CanListStays\").DisableAntiforgery().OwnerPhotoLimited().PhotoBodyLimited();",
            Src("RentNearBy.Api", "Endpoints", "StayEndpoints.cs"));

    [Fact]
    public void ReplacePhotoSql_keeps_order_and_skips_hidden()
    {
        Assert.Contains("NOT p.\"HiddenByPlan\"", StayHandlers.ReplacePhotoSql);
        Assert.Contains("p.\"StayListingId\" = @s", StayHandlers.ReplacePhotoSql);
        Assert.Contains("FOR UPDATE", StayHandlers.ReplacePhotoSql);
        Assert.Contains("RETURNING old.\"FilePath\" AS \"OldPath\"", StayHandlers.ReplacePhotoSql);
        Assert.DoesNotContain("\"PhotoOrder\"", StayHandlers.ReplacePhotoSql);
        Assert.DoesNotContain("\"Id\" =", StayHandlers.ReplacePhotoSql.Split("SET")[1].Split("FROM")[0]);
    }

    // Ownership in the read, review lock and file checks before the upload, stay lock before the swap, old file only after the commit.
    [Fact]
    public void Replace_handler_order()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"), "public static async Task<IResult> ReplacePhoto(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.Contains("s.UserId == userId && !s.IsDeleted", body);
        Assert.Contains("AsNoTracking()", body);
        Assert.True(At("StayStatuses.PendingReview") < At("SavePhotoAsync"));
        Assert.True(At("IsValidImageMagicBytes") < At("SavePhotoAsync"));
        Assert.True(At("SavePhotoAsync(stream, photo.FileName, userId, stay.Id)") > 0);
        Assert.True(At("OwnerLocks.LockStayAsync") < At("ReplacePhotoSql"));
        Assert.True(At("ReplacePhotoSql") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("TryDeleteAsync(photoService, oldPath"));
        Assert.Contains("TryDeleteAsync(photoService, filePath", body);
        Assert.DoesNotContain("PublishAsync", body);
        Assert.DoesNotContain("SubmittedAt", body);
        Assert.DoesNotContain("LockOwnerAsync", body);
    }

    // R8: the existing delete removes the file after the row commit.
    [Fact]
    public void Delete_photo_removes_the_file_after_the_commit()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"), "public static async Task<IResult> DeletePhoto(");
        var commit = body.IndexOf("CommitTransactionAsync", StringComparison.Ordinal);
        var refusedReturn = body.IndexOf("if (refused != null) return refused;", StringComparison.Ordinal);
        var delete = body.IndexOf("PhotoCleanup.TryDeleteAsync(photoService, photo.FilePath", StringComparison.Ordinal);
        Assert.True(commit > 0 && refusedReturn > commit && delete > refusedReturn);
    }

    // R8: whole-stay delete calls the listing prefix delete (bakhli/user_{u}/listing_{id}/) after the commit.
    [Fact]
    public void Whole_stay_delete_calls_prefix_delete_after_the_commit()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"), "public static async Task<ListingRemovalResult> SoftDeleteStayAsync(");
        var commit = body.IndexOf("CommitTransactionAsync", StringComparison.Ordinal);
        var prefix = body.IndexOf("listingPurge.DeleteListingPhotosBestEffortAsync(stay.UserId, [stay.Id])", StringComparison.Ordinal);
        Assert.True(commit > 0 && prefix > commit);
    }

    [Fact]
    public async Task Prefix_delete_reaches_the_photo_service_for_each_stay()
    {
        var photos = new RecordingPhotoService();
        var purge = new ListingPurgeService(null!, photos, null!, NullLogger<ListingPurgeService>.Instance);
        var user = Guid.NewGuid();
        var stay = Guid.NewGuid();

        await purge.DeleteListingPhotosBestEffortAsync(user, [stay]);

        Assert.Equal([(user, stay)], photos.ListingPrefixDeletes.ToArray());
    }

    [Fact]
    public void Stay_photos_are_saved_under_the_listing_prefix()
        => Assert.Contains("SavePhotoAsync(stream, photo.FileName, userId, stay.Id)", Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"));
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class StayPhotoReplaceIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0];

    private static ClaimsPrincipal Principal(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static int StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static IFormFile File(byte[] bytes, long? length = null)
        => new FormFile(new MemoryStream(bytes), 0, length ?? bytes.Length, "photo", "new.jpg");

    private static IServiceProvider Services(FakeRedis? redis = null)
    {
        var s = new ServiceCollection();
        if (redis != null) s.AddSingleton(redis.Connection);
        return s.BuildServiceProvider();
    }

    private async Task<IResult> ReplaceAsync(Guid userId, Guid photoId, IPhotoService photos, IFormFile? file = null, FakeRedis? redis = null)
    {
        await using var c = CreateContext();
        return await StayHandlers.ReplacePhoto(photoId, file ?? File(Jpeg), Principal(userId), new UnitOfWork(c), c, photos, Services(redis));
    }

    [IntegrationFact]
    public async Task Replace_keeps_id_and_order_and_deletes_old_file()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> seeded;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            seeded = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 3);
        }
        var photos = new RecordingPhotoService();

        var result = await ReplaceAsync(owner.UserId, seeded[1].Id, photos);

        Assert.Equal(200, StatusOf(result));
        var saved = Assert.Single(photos.Saved);
        Assert.StartsWith($"bakhli/user_{owner.UserId}/listing_{owner.ListingId}/", saved);
        Assert.Equal(["p1"], photos.Deleted.ToArray());
        await using var check = CreateContext();
        var rows = await check.StayPhotos.AsNoTracking().Where(p => p.StayListingId == owner.ListingId).OrderBy(p => p.PhotoOrder).ToListAsync();
        Assert.Equal(seeded.Select(p => p.Id), rows.Select(p => p.Id));
        Assert.Equal([0, 1, 2], rows.Select(p => p.PhotoOrder));
        Assert.Equal(saved, rows[1].FilePath);
        Assert.Equal("p0", rows[0].FilePath);
        Assert.Equal("p2", rows[2].FilePath);
    }

    [IntegrationFact]
    public async Task Replace_foreign_or_hidden_photo_is_404_and_deletes_new_file()
    {
        OwnerSeed.OwnerRow owner, other;
        List<StayPhoto> mine, theirs;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            mine = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1, hidden: 1);
            other = await OwnerSeed.StayOwnerAsync(c);
            theirs = await OwnerSeed.PhotosAsync(c, other.ListingId, visible: 1);
        }

        foreach (var photoId in new[] { theirs[0].Id, mine.Single(p => p.HiddenByPlan).Id, Guid.NewGuid() })
        {
            var photos = new RecordingPhotoService();
            var result = await ReplaceAsync(owner.UserId, photoId, photos);
            Assert.Equal(404, StatusOf(result));
            Assert.Equal(photos.Saved.ToArray(), photos.Deleted.ToArray());
        }
        await using var check = CreateContext();
        Assert.Equal("p0", (await check.StayPhotos.AsNoTracking().SingleAsync(p => p.Id == theirs[0].Id)).FilePath);
        Assert.Equal("p1", (await check.StayPhotos.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == mine[1].Id)).FilePath);
    }

    [IntegrationFact]
    public async Task Replace_under_review_is_409_and_uploads_nothing()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> seeded;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c, StayStatuses.PendingReview);
            seeded = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
        }
        var photos = new RecordingPhotoService();

        Assert.Equal(409, StatusOf(await ReplaceAsync(owner.UserId, seeded[0].Id, photos)));
        Assert.Empty(photos.Saved);
        Assert.Empty(photos.Deleted);
    }

    // REVIEW_SEC S5: Submit commits while the file uploads; the swap re-checks the review lock under the row lock.
    [IntegrationFact]
    public async Task Replace_when_submit_lands_during_upload_is_409_and_deletes_new_file()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> seeded;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c, StayStatuses.Draft);
            seeded = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
        }
        var photos = new SubmitDuringSave(async () =>
        {
            await using var c = CreateContext();
            await c.StayListings.Where(s => s.Id == owner.ListingId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StayStatuses.PendingReview));
        });

        Assert.Equal(409, StatusOf(await ReplaceAsync(owner.UserId, seeded[0].Id, photos)));
        Assert.Equal(photos.Saved.ToArray(), photos.Deleted.ToArray());
        await using var check = CreateContext();
        Assert.Equal("p0", (await check.StayPhotos.AsNoTracking().SingleAsync(p => p.Id == seeded[0].Id)).FilePath);
    }

    private sealed class SubmitDuringSave(Func<Task> afterSave) : IPhotoService
    {
        private readonly RecordingPhotoService _inner = new();
        public IReadOnlyCollection<string> Saved => _inner.Saved;
        public IReadOnlyCollection<string> Deleted => _inner.Deleted;

        public async Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
        {
            var saved = await _inner.SavePhotoAsync(photoStream, fileName, userId, listingId);
            await afterSave();
            return saved;
        }

        public Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId)
            => _inner.SaveBannerAsync(photoStream, fileName, districtId);
        public Task DeletePhotoAsync(string filePath) => _inner.DeletePhotoAsync(filePath);
        public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId) => _inner.DeleteRoomPhotosAsync(userId, listingId);
        public Task DeleteAllUserPhotosAsync(Guid userId) => _inner.DeleteAllUserPhotosAsync(userId);
        public Task<bool> PingAsync() => Task.FromResult(true);
    }

    [IntegrationFact]
    public async Task Replace_wrong_bytes_or_over_10MB_is_400_and_saves_nothing()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> seeded;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            seeded = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
        }
        var photos = new RecordingPhotoService();

        Assert.Equal(400, StatusOf(await ReplaceAsync(owner.UserId, seeded[0].Id, photos, File("GIF89a-not-allowed"u8.ToArray()))));
        Assert.Equal(400, StatusOf(await ReplaceAsync(owner.UserId, seeded[0].Id, photos, File(Jpeg, 10L * 1024 * 1024 + 1))));
        Assert.Empty(photos.Saved);
    }

    [IntegrationFact]
    public async Task Replace_on_live_stay_keeps_it_live_and_bumps_cache()
    {
        OwnerSeed.OwnerRow live, draft;
        List<StayPhoto> livePhotos, draftPhotos;
        await using (var c = CreateContext())
        {
            live = await OwnerSeed.StayOwnerAsync(c);
            livePhotos = await OwnerSeed.PhotosAsync(c, live.ListingId, visible: 1);
            draft = await OwnerSeed.StayOwnerAsync(c, StayStatuses.Draft);
            draftPhotos = await OwnerSeed.PhotosAsync(c, draft.ListingId, visible: 1);
        }

        var redis = new FakeRedis();
        redis.Store[StayCache.RecentKey] = ("[]", null);
        Assert.Equal(200, StatusOf(await ReplaceAsync(draft.UserId, draftPhotos[0].Id, new RecordingPhotoService(), redis: redis)));
        Assert.True(redis.Has(StayCache.RecentKey));

        Assert.Equal(200, StatusOf(await ReplaceAsync(live.UserId, livePhotos[0].Id, new RecordingPhotoService(), redis: redis)));
        Assert.False(redis.Has(StayCache.RecentKey));

        await using var check = CreateContext();
        var stay = await check.StayListings.AsNoTracking().SingleAsync(s => s.Id == live.ListingId);
        Assert.Equal(StayStatuses.Approved, stay.Status);
    }

    [IntegrationFact]
    public async Task Delete_photo_removes_cloudinary_file()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> seeded;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            seeded = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2);
        }
        var photos = new RecordingPhotoService();

        await using (var c = CreateContext())
        {
            var catalog = new Catalog(OwnerPlanCatalog.Seeded());
            var payment = new Payment(false);
            var entitlements = new PlanEntitlementService(c, catalog, payment);
            var reconcile = new PlanLimitsReconcile(c, catalog, payment, Closure(c), new NullNotifier(), NullLogger<PlanLimitsReconcile>.Instance);
            var slots = new PlanSlotService(c, entitlements, catalog, payment, reconcile);
            var result = await StayHandlers.DeletePhoto(seeded[0].Id, Principal(owner.UserId), new UnitOfWork(c), c, photos, slots, entitlements,
                reconcile, Services());
            Assert.Equal(204, StatusOf(result));
        }

        Assert.Equal(["p0"], photos.Deleted.ToArray());
        await using var check = CreateContext();
        Assert.False(await check.StayPhotos.IgnoreQueryFilters().AnyAsync(p => p.Id == seeded[0].Id));
    }

    [IntegrationFact]
    public async Task Whole_stay_delete_calls_prefix_delete()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2);
        }
        var photos = new RecordingPhotoService();

        await using (var c = CreateContext())
        {
            var purge = new ListingPurgeService(c, photos, new NullOwnerPurge(), NullLogger<ListingPurgeService>.Instance);
            await purge.DeleteListingPhotosBestEffortAsync(owner.UserId, [owner.ListingId]);
        }

        Assert.Equal([(owner.UserId, owner.ListingId)], photos.ListingPrefixDeletes.ToArray());
        Assert.All(photos.Saved, p => Assert.StartsWith($"bakhli/user_{owner.UserId}/listing_{owner.ListingId}/", p));
    }

    [IntegrationFact]
    public async Task Feed_and_browse_offer_fields_same_query_count_at_3_and_30_rows()
    {
        var today = IstTime.TodayIst(DateTime.UtcNow);
        var ids = new List<Guid>();
        await using (var c = CreateContext())
        {
            for (var i = 0; i < 30; i++)
            {
                var owner = await OwnerSeed.StayOwnerAsync(c, name: $"Offer stay {i}");
                await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
                await OwnerSeed.SpecialPricesAsync(c, owner.ListingId, 1, today.AddDays(2), percent: 15);
                ids.Add(owner.ListingId);
            }
        }

        foreach (var n in new[] { 3, 30 })
        {
            var rows = ids.Take(n).Select(id => new NearbyRow(id, 500)).ToList();
            await using var counting = Db.CreateCountingContext(out var counter);
            counter.Reset();
            var feed = await StayFeedHydrator.LoadAsync(counting, rows);
            Assert.Equal(1, counter.Count);
            Assert.Equal(n, feed.Count);
            Assert.All(feed.Values, f =>
            {
                Assert.Equal(3000, f.OldPricePerNight);
                Assert.Equal(StaySpecialPriceMath.Adjust(3000, true, 15), f.OfferPricePerNight);
                Assert.Equal(15, f.OfferPercent);
                Assert.Equal(3000, f.MinPricePerNight);
            });

            counter.Reset();
            var recent = await new StayListingRepository(counting).GetRecentProjectedAsync(n);
            Assert.Equal(1, counter.Count);
            Assert.All(recent.Where(r => ids.Contains(r.Id)), r => Assert.Equal(15, r.OfferPercent));
        }
    }

    private sealed class Catalog(PlanCatalogSnapshot snapshot) : IPlanCatalogCache
    {
        public Task<PlanCatalogSnapshot> GetAsync() => Task.FromResult(snapshot);
        public Task<PlanCatalogSnapshot> RefreshAsync() => Task.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class Payment(bool on) : IPaymentFeatureReader
    {
        public Task<PaymentFeatureState> GetAsync() => Task.FromResult(on ? PaymentFeatureStates.On() : PaymentFeatureStates.Off());
        public void Invalidate() { }
    }

    private sealed class NullNotifier : IOwnerNotifier
    {
        public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default) => Task.CompletedTask;
        public void Wake() { }
    }

    private sealed class NullPushWriter : ITaxiPushWriter
    {
        public Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default) => Task.CompletedTask;
        public Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullOwnerPurge : IOwnerListingPurge
    {
        public Task PurgeAsync(string listingKind, IReadOnlyCollection<Guid> listingIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TaxiRequestClosure Closure(ApplicationDbContext db)
    {
        var s = new TaxiOfferKit.NullSignals();
        var writer = new NullPushWriter();
        var store = new TaxiRequestStore(writer, new DueActionScheduler(), s, s, s, s, NullLogger<TaxiRequestStore>.Instance);
        return new TaxiRequestClosure(store, writer, new DueActionScheduler(), new TaxiPickupPlaces(db, null!), NullLogger<TaxiRequestClosure>.Instance);
    }
}

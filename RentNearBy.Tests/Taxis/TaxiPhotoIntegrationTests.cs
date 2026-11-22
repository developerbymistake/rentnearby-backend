using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Api.Mappings;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;
using StackExchange.Redis;

namespace RentNearBy.Tests.Taxis;

// CONTRACT_BACKEND (card photos) section 9 BE1. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on this machine.
[Trait("Category", "Integration")]
public class TaxiPhotoIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static IFormFile Upload(byte[] bytes, long? length = null)
        => new FormFile(new MemoryStream(bytes), 0, length ?? bytes.Length, "photo", "photo.jpg");

    private static IServiceProvider Services(IConnectionMultiplexer? redis = null)
    {
        var services = new ServiceCollection();
        if (redis != null) services.AddSingleton(redis);
        return services.BuildServiceProvider();
    }

    private static int Status(IResult r) => JourneyHarness.StatusOf(r);

    private static string? ErrorType(IResult r)
    {
        var body = (r as IValueHttpResult)?.Value;
        return (body?.GetType().GetProperty("Error")?.GetValue(body) as ApiError)?.Type;
    }

    private async Task SetAsync(Guid taxiId, Action<TaxiListing> change)
    {
        await using var c = CreateContext();
        var t = await c.TaxiListings.SingleAsync(x => x.Id == taxiId);
        change(t);
        await c.SaveChangesAsync();
    }

    private async Task<TaxiListing> ReadAsync(Guid taxiId)
    {
        await using var c = CreateContext();
        return await c.TaxiListings.AsNoTracking().SingleAsync(x => x.Id == taxiId);
    }

    private async Task<IResult> PlateAsync(Guid userId, IPhotoService photos, IFormFile? file = null)
    {
        await using var c = CreateContext();
        return await TaxiHandlers.UploadPlatePhoto(file ?? Upload(Jpeg), JourneyHarness.Principal(userId), new UnitOfWork(c), photos);
    }

    private async Task<IResult> DisplayAsync(Guid userId, IPhotoService photos, IFormFile? file = null, IConnectionMultiplexer? redis = null)
    {
        await using var c = CreateContext();
        return await TaxiHandlers.UploadDisplayPhoto(file ?? Upload(Jpeg), JourneyHarness.Principal(userId), c, photos, Services(redis));
    }

    [IntegrationFact]
    public async Task Plate_replace_before_first_approval_deletes_old_file()
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Draft);
        await SetAsync(driver.ListingId, t => { t.PlatePhotoUrl = "https://res.cloudinary.com/x/image/upload/old.jpg"; t.PlatePhotoPath = "old"; });
        var photos = new RecordingPhotoService();

        var result = await PlateAsync(driver.UserId, photos);

        Assert.Equal(201, Status(result));
        var row = await ReadAsync(driver.ListingId);
        Assert.Equal(photos.Saved.Single(), row.PlatePhotoPath);
        Assert.Equal(["old"], photos.Deleted.ToArray());
        Assert.Equal(TaxiStatuses.Draft, row.Status);
    }

    [IntegrationTheory]
    [InlineData(TaxiStatuses.Approved)]
    [InlineData(TaxiStatuses.PendingReview)]
    [InlineData(TaxiStatuses.Rejected)]
    public async Task Plate_upload_after_ever_approved_is_409_TaxiProofLocked_and_uploads_nothing(string status)
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, status);
        await SetAsync(driver.ListingId, t => { t.EverApproved = true; t.PlatePhotoPath = "kept"; t.PlatePhotoUrl = "https://kept"; });
        var photos = new RecordingPhotoService();

        var result = await PlateAsync(driver.UserId, photos);

        Assert.Equal(409, Status(result));
        Assert.Equal(TaxiErrorTypes.TaxiProofLocked, ErrorType(result));
        Assert.Empty(photos.Saved);
        Assert.Empty(photos.Deleted);
        var row = await ReadAsync(driver.ListingId);
        Assert.Equal("kept", row.PlatePhotoPath);
        Assert.Equal(status, row.Status);
    }

    [IntegrationFact]
    public async Task Plate_upload_racing_submit_returns_concurrent_and_deletes_new_file()
    {
        await using var seed = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(seed, TaxiStatuses.Draft);
        // Submit lands between the handler's read and its save: the tracked row's xmin is stale.
        var raced = new RacingPhotoService(() => SetAsync(driver.ListingId, t => t.Status = TaxiStatuses.PendingReview));

        var result = await PlateAsync(driver.UserId, raced);

        Assert.Equal(409, Status(result));
        Assert.Equal("CONCURRENT_UPDATE", ErrorType(result));
        Assert.Equal(raced.Inner.Saved.ToArray(), raced.Inner.Deleted.ToArray());
        var retry = await PlateAsync(driver.UserId, new RecordingPhotoService());
        Assert.Equal(TaxiErrorTypes.TaxiLocked, ErrorType(retry));
    }

    // REVIEW_SEC S1: under the first review the proof is frozen, so the admin approves the file he saw.
    [IntegrationFact]
    public async Task Plate_upload_under_first_review_is_409_and_uploads_nothing()
    {
        await using var seed = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(seed, TaxiStatuses.PendingReview);
        await SetAsync(driver.ListingId, t => { t.EverApproved = false; t.PlatePhotoPath = "reviewed"; t.PlatePhotoUrl = "https://reviewed"; });
        var photos = new RecordingPhotoService();

        var result = await PlateAsync(driver.UserId, photos);

        Assert.Equal(409, Status(result));
        Assert.Equal(TaxiErrorTypes.TaxiLocked, ErrorType(result));
        Assert.Empty(photos.Saved);
        Assert.Equal("reviewed", (await ReadAsync(driver.ListingId)).PlatePhotoPath);
    }

    // REVIEW_SEC S8: the admin removes only the display photo; the taxi stays live, the proof stays, the file goes.
    [IntegrationFact]
    public async Task Admin_remove_display_photo_clears_columns_keeps_status_and_deletes_file()
    {
        await using var seed = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(seed, TaxiStatuses.Approved);
        await SetAsync(driver.ListingId, t =>
        {
            t.DisplayPhotoUrl = "https://face"; t.DisplayPhotoPath = "face"; t.PlatePhotoPath = "proof"; t.PlatePhotoUrl = "https://proof";
        });
        var photos = new RecordingPhotoService();

        await using (var c = CreateContext())
        {
            var result = await AdminTaxiHandlers.RemoveDisplayPhoto(driver.ListingId, new UnitOfWork(c), c, photos, Services(),
                new NullHubContext(), new RecordingPublisher());
            Assert.Equal(204, Status(result));
        }

        var row = await ReadAsync(driver.ListingId);
        Assert.Null(row.DisplayPhotoUrl);
        Assert.Null(row.DisplayPhotoPath);
        Assert.Equal("proof", row.PlatePhotoPath);
        Assert.Equal(TaxiStatuses.Approved, row.Status);
        Assert.Equal(["face"], photos.Deleted.ToArray());
        await using var check = CreateContext();
        Assert.True(await check.NotificationEvents.AnyAsync(n => n.TargetUserId == driver.UserId && n.Type == NotificationTypes.DriverPhotoRemoved));

        await using var again = CreateContext();
        var second = await AdminTaxiHandlers.RemoveDisplayPhoto(driver.ListingId, new UnitOfWork(again), again, photos, Services(),
            new NullHubContext(), new RecordingPublisher());
        Assert.Equal(404, Status(second));
    }

    [IntegrationFact]
    public async Task Display_upload_sets_columns_keeps_status_and_deletes_old_file()
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        await SetAsync(driver.ListingId, t => { t.DisplayPhotoPath = "old-face"; t.DisplayPhotoUrl = "https://old"; });
        var outboxBefore = await c.OutboxMessages.CountAsync();
        var photos = new RecordingPhotoService();

        var result = await DisplayAsync(driver.UserId, photos);

        Assert.Equal(201, Status(result));
        var row = await ReadAsync(driver.ListingId);
        Assert.Equal(TaxiStatuses.Approved, row.Status);
        Assert.Null(row.SubmittedAt);
        Assert.Equal(photos.Saved.Single(), row.DisplayPhotoPath);
        Assert.EndsWith(".jpg", row.DisplayPhotoUrl);
        Assert.Equal(["old-face"], photos.Deleted.ToArray());
        Assert.Equal(outboxBefore, await c.OutboxMessages.CountAsync());
    }

    [IntegrationFact]
    public async Task Display_upload_on_deleted_taxi_is_404_and_deletes_new_file()
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        // Deleted between the projected read (Q1) and the swap (Q2).
        var raced = new RacingPhotoService(() => SetAsync(driver.ListingId, t => t.IsDeleted = true));

        var result = await DisplayAsync(driver.UserId, raced);

        Assert.Equal(404, Status(result));
        Assert.Equal(raced.Inner.Saved.ToArray(), raced.Inner.Deleted.ToArray());
        Assert.Null((await ReadAsync(driver.ListingId)).DisplayPhotoPath);
    }

    [IntegrationFact]
    public async Task Display_upload_two_in_parallel_leave_exactly_one_file()
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        var photos = new RecordingPhotoService();

        var results = await Task.WhenAll(DisplayAsync(driver.UserId, photos), DisplayAsync(driver.UserId, photos));

        Assert.All(results, r => Assert.Equal(201, Status(r)));
        var kept = (await ReadAsync(driver.ListingId)).DisplayPhotoPath;
        Assert.Equal([kept!], photos.Saved.Except(photos.Deleted).ToList());
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Display_upload_wrong_bytes_or_over_10MB_is_400_and_saves_nothing(bool tooBig)
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        var photos = new RecordingPhotoService();
        var file = tooBig ? Upload(Jpeg, TaxiPhotoRules.MaxBytes + 1) : Upload("GIF89a-not-allowed"u8.ToArray());

        var result = await DisplayAsync(driver.UserId, photos, file);

        Assert.Equal(400, Status(result));
        Assert.Empty(photos.Saved);
    }

    [IntegrationTheory]
    [InlineData(TaxiStatuses.Approved, true)]
    [InlineData(TaxiStatuses.Draft, false)]
    public async Task Display_upload_bumps_taxi_cache_generation_only_when_approved(string status, bool bumped)
    {
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, status);
        var redis = RecordingRedis.Create(out var keys);

        Assert.Equal(201, Status(await DisplayAsync(driver.UserId, new RecordingPhotoService(), redis: redis)));

        Assert.Equal(bumped, keys.Contains(TaxiCache.SearchGenKey));
    }

    // The search route needs route geometry rows; its photo column rides in the same SELECT (TaxiPhotoWireTests pins the DTO).
    [IntegrationFact]
    public async Task Browse_search_feed_have_display_photo_with_same_query_count_at_3_and_30_rows()
    {
        var district = Guid.NewGuid();
        var ids = new List<Guid>();
        async Task Seed(ApplicationDbContext ctx, int rows)
        {
            if (!await ctx.Districts.AnyAsync(d => d.Id == district))
            {
                ctx.Districts.Add(new District { Id = district, Name = "D" + district.ToString("N")[..6], StateName = "S", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
            }
            ids.Clear();
            for (var i = 0; i < rows; i++)
            {
                var d = await OwnerSeed.TaxiDriverAsync(ctx, TaxiStatuses.Approved);
                var url = "https://res.cloudinary.com/x/image/upload/f" + i + ".jpg";
                await ctx.TaxiListings.Where(t => t.Id == d.ListingId).ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.DistrictId, district)
                    .SetProperty(t => t.DisplayPhotoUrl, url));
                ids.Add(d.ListingId);
            }
        }

        await QueryAssert.SameCountAsync(Db, Seed, async ctx =>
        {
            var (items, _) = await new TaxiListingRepository(ctx).BrowseLocalPagedAsync(district, null, null, 1, 30);
            Assert.All(items, i => Assert.StartsWith("https://res.cloudinary.com/x/image/upload/" + CloudinaryThumb.TaxiFace, i.DisplayPhotoUrl));
        }, budget: 1);

        await QueryAssert.SameCountAsync(Db, Seed, async ctx =>
        {
            var feed = await TaxiFeedHydrator.LoadAsync(ctx, ids.Select(id => new NearbyRow(id, 1000)).ToList());
            Assert.All(feed.Values, i => Assert.NotNull(i.DisplayPhotoUrl));
        }, budget: 1);
    }

    [IntegrationFact]
    public async Task Taxi_soft_delete_clears_both_urls_and_calls_prefix_delete()
    {
        await using var h = new JourneyHarness(Db);
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        await SetAsync(driver.ListingId, t =>
        {
            t.PlatePhotoUrl = "https://p"; t.PlatePhotoPath = "p"; t.DisplayPhotoUrl = "https://d"; t.DisplayPhotoPath = "d";
        });
        var photos = new RecordingPhotoService();
        await using var s = h.NewScope();
        var taxi = await s.Uow.Taxis.GetLiveByUserAsync(driver.UserId);
        var purge = new ListingPurgeService(s.Db, photos, new NullOwnerListingPurge(), NullLogger<ListingPurgeService>.Instance);

        var removed = await ListingRemoval.SoftDeleteTaxiAsync(taxi!, s.Uow, s.Db, s.Closure, new NullOwnerListingPurge(), purge, h.Services);

        Assert.False(removed.Concurrent);
        var row = await ReadAsync(driver.ListingId);
        Assert.True(row.IsDeleted);
        Assert.Null(row.PlatePhotoUrl);
        Assert.Null(row.PlatePhotoPath);
        Assert.Null(row.DisplayPhotoUrl);
        Assert.Null(row.DisplayPhotoPath);
        Assert.Contains((driver.UserId, driver.ListingId), photos.ListingPrefixDeletes);
    }

    [IntegrationFact]
    public async Task Account_deletion_calls_user_prefix_delete()
    {
        await using var h = new JourneyHarness(Db);
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Draft);
        await SetAsync(driver.ListingId, t => { t.DisplayPhotoPath = "d"; t.PlatePhotoPath = "p"; });
        var photos = new RecordingPhotoService();
        await using var s = h.NewScope();
        var purge = new ListingPurgeService(s.Db, photos, new NullOwnerListingPurge(), NullLogger<ListingPurgeService>.Instance);
        var service = new AccountDeletionService(s.Db, purge, new PlaySubscriptionRepository(s.Db),
            Options.Create(new PlayBillingOptions()), s.Closure,
            new ListingCacheInvalidator(s.Db, h.Services, NullLogger<ListingCacheInvalidator>.Instance), h.Services);

        await service.DeleteAccountAsync(driver.UserId);

        Assert.Contains(driver.UserId, photos.UserPrefixDeletes);
    }

    [IntegrationFact]
    public async Task Public_detail_and_slug_never_return_plate_photo()
    {
        DtoMappings.ConfigureMappings();
        await using var c = CreateContext();
        var driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.Approved);
        var slug = "photo-" + driver.ListingId.ToString("N")[..10];
        await SetAsync(driver.ListingId, t => { t.Slug = slug; t.PlatePhotoUrl = "https://plate"; t.DisplayPhotoUrl = "https://face"; });

        await using var r = CreateContext();
        var detail = await TaxiHandlers.GetById(driver.ListingId, new UnitOfWork(r), new System.Security.Claims.ClaimsPrincipal());
        var bySlug = await TaxiHandlers.GetBySlug(slug, new UnitOfWork(r));

        foreach (var result in new[] { detail, bySlug })
        {
            var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain("https://plate", json);
            Assert.DoesNotContain("platePhotoUrl", json);
            Assert.Contains("\"displayPhotoUrl\":\"https://face\"", json);
        }
    }

    // The hub send is best effort in production (caught there); this context has no clients.
    private sealed class NullHubContext : Microsoft.AspNetCore.SignalR.IHubContext<RentNearBy.Api.Hubs.NotificationHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => null!;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => null!;
    }

    // Runs another request right after the file is saved, then records like RecordingPhotoService.
    private sealed class RacingPhotoService(Func<Task> afterSave) : IPhotoService
    {
        public RecordingPhotoService Inner { get; } = new();

        public async Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
        {
            var saved = await Inner.SavePhotoAsync(photoStream, fileName, userId, listingId);
            await afterSave();
            return saved;
        }

        public Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId)
            => Inner.SaveBannerAsync(photoStream, fileName, districtId);
        public Task DeletePhotoAsync(string filePath) => Inner.DeletePhotoAsync(filePath);
        public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId) => Inner.DeleteRoomPhotosAsync(userId, listingId);
        public Task DeleteAllUserPhotosAsync(Guid userId) => Inner.DeleteAllUserPhotosAsync(userId);
        public Task<bool> PingAsync() => Task.FromResult(true);
    }

    // Redis that answers every call with a default value and records the keys passed to it.
    public class RecordingRedis : DispatchProxy
    {
        private List<string> _keys = null!;
        private IDatabase _db = null!;

        public static IConnectionMultiplexer Create(out List<string> keys)
        {
            keys = [];
            var db = DispatchProxy.Create<IDatabase, RecordingRedis>();
            ((RecordingRedis)(object)db)._keys = keys;
            var mux = DispatchProxy.Create<IConnectionMultiplexer, RecordingRedis>();
            ((RecordingRedis)(object)mux)._keys = keys;
            ((RecordingRedis)(object)mux)._db = db;
            return mux;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "GetDatabase") return _db;
            lock (_keys)
            {
                foreach (var a in args ?? [])
                {
                    if (a is RedisKey k) _keys.Add(k.ToString());
                    else if (a is RedisKey[] ks) _keys.AddRange(ks.Select(x => x.ToString()));
                }
            }
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                object? value = inner == typeof(RedisValue) ? RedisValue.Null
                    : inner == typeof(RedisValue[]) ? Array.Empty<RedisValue>()
                    : inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}

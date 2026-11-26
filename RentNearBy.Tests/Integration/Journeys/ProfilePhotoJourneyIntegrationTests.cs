using Mapster;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Users;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// DP-TEST: the profile picture life of one user on a real PostgreSQL (RENTNEARBY_TEST_DB).
// WRITTEN, NOT RUN on this machine (no PostGIS): upload, replace, admin removal, new upload, account deletion.
[Trait("Category", "Integration")]
public class ProfilePhotoJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    // Every save gets a new path (the cloud does the same); records saves and deletes.
    private sealed class FreshPathPhotoService : IPhotoService
    {
        private readonly RecordingPhotoService _inner = new();
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<Guid> UserPrefixDeletes { get; } = [];

        public Task<(string url, string filePath)> SaveProfilePhotoAsync(Stream photoStream, string fileName, Guid userId)
        {
            var path = $"bakhli/user_{userId}/profile/{Guid.NewGuid():N}";
            Saved.Add(path);
            return Task.FromResult(($"https://res.cloudinary.com/test/image/upload/v1/{path}.jpg", path));
        }

        public Task DeletePhotoAsync(string filePath) { Deleted.Add(filePath); return Task.CompletedTask; }
        public Task DeleteAllUserPhotosAsync(Guid userId) { UserPrefixDeletes.Add(userId); return Task.CompletedTask; }
        public Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
            => _inner.SavePhotoAsync(photoStream, fileName, userId, listingId);
        public Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId)
            => _inner.SaveBannerAsync(photoStream, fileName, districtId);
        public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId) => _inner.DeleteRoomPhotosAsync(userId, listingId);
        public Task<bool> PingAsync() => Task.FromResult(true);
    }

    // The hub send is best effort in production (caught there); this context has no clients.
    private sealed class NoClientsHub : Microsoft.AspNetCore.SignalR.IHubContext<RentNearBy.Api.Hubs.NotificationHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => null!;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => null!;
    }

    private static IFormFile Jpeg()
    {
        var bytes = TestImages.SmallJpeg();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photo", "me.jpg");
    }

    private static IServiceProvider Services()
        => new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider();

    private async Task<UserDto> UploadAsync(JourneyHarness h, Guid userId, IPhotoService photos)
    {
        MappingsOnce.Ensure();
        await using var c = CreateContext();
        var result = await UserPhotoHandlers.UploadPhoto(Jpeg(), Principal(userId), c, new UnitOfWork(c), photos, h.Cache,
            new UsernameService(new UnitOfWork(c), new FakePendingStore()), Services());
        Assert.Equal(200, StatusOf(result));
        return DataOf<UserDto>(result);
    }

    private async Task<(string? Url, string? Path, DateTime? At)> ReadAsync(Guid userId)
    {
        await using var c = CreateContext();
        var u = await c.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        return (u.ProfilePhotoUrl, u.ProfilePhotoPath, u.ProfilePhotoUpdatedAt);
    }

    private async Task<int> RemoveAsync(Guid userId, IPhotoService photos)
    {
        await using var c = CreateContext();
        var result = await AdminUserPhotoHandlers.RemoveProfilePhoto(userId, new DefaultHttpContext { RequestServices = Services() },
            new UnitOfWork(c), c, photos, Services(), new NoClientsHub(), new RecordingPublisher());
        return StatusOf(result);
    }

    [IntegrationFact]
    public async Task First_upload_stores_the_picture_and_deletes_nothing()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddSeekerAsync();
        var photos = new FreshPathPhotoService();

        var dto = await UploadAsync(h, userId, photos);

        var row = await ReadAsync(userId);
        Assert.Equal(photos.Saved.Single(), row.Path);
        Assert.NotNull(row.At);
        Assert.False(string.IsNullOrEmpty(dto.ProfilePhotoUrl));
        Assert.False(dto.ProfilePhotoRemoved);
        Assert.Empty(photos.Deleted);
    }

    [IntegrationFact]
    public async Task Replacing_the_picture_deletes_the_old_cloudinary_file_after_the_row_changed()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddSeekerAsync();
        var photos = new FreshPathPhotoService();
        await UploadAsync(h, userId, photos);
        var firstPath = (await ReadAsync(userId)).Path;

        await UploadAsync(h, userId, photos);

        var row = await ReadAsync(userId);
        Assert.Equal(2, photos.Saved.Count);
        Assert.Equal(photos.Saved[1], row.Path);
        Assert.Equal([firstPath], photos.Deleted.ToArray());
    }

    [IntegrationFact]
    public async Task Admin_removal_clears_the_picture_writes_the_notice_deletes_the_file_and_the_user_adds_a_new_one()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddStayOwnerAsync();
        var photos = new FreshPathPhotoService();
        await UploadAsync(h, userId, photos);
        var storedPath = (await ReadAsync(userId)).Path;

        Assert.Equal(204, await RemoveAsync(userId, photos));

        var row = await ReadAsync(userId);
        Assert.Null(row.Url);
        Assert.Null(row.Path);
        Assert.NotNull(row.At);
        Assert.Equal([storedPath], photos.Deleted.ToArray());
        await using (var check = CreateContext())
        {
            Assert.True(await check.NotificationEvents.AnyAsync(n => n.TargetUserId == userId && n.Type == NotificationTypes.ProfilePhotoRemoved));
            var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            MappingsOnce.Ensure();
            var dto = user.Adapt<UserDto>();
            Assert.Null(dto.ProfilePhotoUrl);
            Assert.True(dto.ProfilePhotoRemoved);
        }

        Assert.Equal(404, await RemoveAsync(userId, photos));

        var fresh = await UploadAsync(h, userId, photos);
        Assert.False(fresh.ProfilePhotoRemoved);
        Assert.False(string.IsNullOrEmpty(fresh.ProfilePhotoUrl));
        Assert.Single(photos.Deleted);
    }

    [IntegrationFact]
    public async Task Own_remove_clears_the_picture_without_the_admin_ask_and_is_idempotent()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddSeekerAsync();
        var photos = new FreshPathPhotoService();
        await UploadAsync(h, userId, photos);
        var storedPath = (await ReadAsync(userId)).Path;

        async Task<UserDto> OwnRemoveAsync()
        {
            MappingsOnce.Ensure();
            await using var c = CreateContext();
            var result = await UserPhotoHandlers.RemovePhoto(Principal(userId), c, new UnitOfWork(c), photos, h.Cache,
                new UsernameService(new UnitOfWork(c), new FakePendingStore()), Services());
            Assert.Equal(200, StatusOf(result));
            return DataOf<UserDto>(result);
        }

        var dto = await OwnRemoveAsync();
        Assert.Null(dto.ProfilePhotoUrl);
        Assert.False(dto.ProfilePhotoRemoved);
        var row = await ReadAsync(userId);
        Assert.Null(row.Url);
        Assert.Null(row.Path);
        Assert.Null(row.At);
        Assert.Equal([storedPath], photos.Deleted.ToArray());

        var again = await OwnRemoveAsync();
        Assert.Null(again.ProfilePhotoUrl);
        Assert.Single(photos.Deleted);
        await using var check = CreateContext();
        Assert.False(await check.NotificationEvents.AnyAsync(n => n.TargetUserId == userId && n.Type == NotificationTypes.ProfilePhotoRemoved));
    }

    [IntegrationFact]
    public async Task Account_deletion_after_an_upload_deletes_the_users_cloudinary_prefix()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddSeekerAsync();
        var photos = new FreshPathPhotoService();
        await UploadAsync(h, userId, photos);

        AccountDeletionResult result;
        await using (var s = h.NewScope())
        {
            var purge = new ListingPurgeService(s.Db, photos, new NullOwnerListingPurge(), NullLogger<ListingPurgeService>.Instance);
            var service = new AccountDeletionService(s.Db, purge, new PlaySubscriptionRepository(s.Db),
                Options.Create(new PlayBillingOptions()), s.Closure,
                new ListingCacheInvalidator(s.Db, h.Services, NullLogger<ListingCacheInvalidator>.Instance), h.Services);
            result = await service.DeleteAccountAsync(userId);
        }

        Assert.True(result.Deleted);
        Assert.Equal([userId], photos.UserPrefixDeletes.ToArray());
        await using var check = CreateContext();
        Assert.False(await check.Users.AnyAsync(u => u.Id == userId));
    }
}

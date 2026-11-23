using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Users;

// CONTRACT_BACKEND_DP 7.13. Needs RENTNEARBY_TEST_DB (PostGIS) and the regenerated InitialCreate; WRITTEN, NOT RUN on this machine.
[Trait("Category", "Integration")]
public class ProfilePhotoIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static IFormFile Jpeg() => new FormFile(new MemoryStream(TestImages.SmallJpeg()), 0, TestImages.SmallJpeg().Length, "photo", "me.jpg");

    private static int Status(IResult r) => JourneyHarness.StatusOf(r);

    private static IMemoryCache Cache()
    {
        MappingsOnce.Ensure();
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(AppTabCache.Key, new List<AppTabDto>());
        return cache;
    }

    private async Task<IResult> UploadAsync(Guid userId, IPhotoService photos)
    {
        await using var c = CreateContext();
        var uow = new UnitOfWork(c);
        return await UserPhotoHandlers.UploadPhoto(Jpeg(), JourneyHarness.Principal(userId), c, uow, photos, Cache(),
            new UsernameService(uow, new MemoryPendingUsernameStore(new MemoryCache(new MemoryCacheOptions()))), new ServiceCollection().BuildServiceProvider());
    }

    private async Task<User> ReadAsync(Guid userId)
    {
        await using var c = CreateContext();
        return await c.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private sealed class NullHubContext : Microsoft.AspNetCore.SignalR.IHubContext<RentNearBy.Api.Hubs.NotificationHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => null!;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => null!;
    }

    // Unique file per save so a replace can be told apart from a re-upload.
    private sealed class CountingPhotoService : IPhotoService
    {
        private int _n;
        public RecordingPhotoService Inner { get; } = new();
        public List<string> Saved { get; } = [];

        public Task<(string url, string filePath)> SaveProfilePhotoAsync(Stream photoStream, string fileName, Guid userId)
        {
            var path = $"bakhli/user_{userId}/profile/p{Interlocked.Increment(ref _n)}";
            lock (Saved) Saved.Add(path);
            return Task.FromResult(($"https://res.cloudinary.com/demo/image/upload/v1/{path}.jpg", path));
        }

        public Task DeletePhotoAsync(string filePath) => Inner.DeletePhotoAsync(filePath);
        public Task<(string url, string filePath)> SavePhotoAsync(Stream s, string f, Guid u, Guid l) => Inner.SavePhotoAsync(s, f, u, l);
        public Task<(string url, string filePath)> SaveBannerAsync(Stream s, string f, Guid d) => Inner.SaveBannerAsync(s, f, d);
        public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId) => Inner.DeleteRoomPhotosAsync(userId, listingId);
        public Task DeleteAllUserPhotosAsync(Guid userId) => Inner.DeleteAllUserPhotosAsync(userId);
        public Task<bool> PingAsync() => Task.FromResult(true);
    }

    [IntegrationFact]
    public async Task Upload_sets_the_columns_and_answers_the_user()
    {
        await using var c = CreateContext();
        var userId = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Asha");
        var photos = new RecordingPhotoService();

        var result = await UploadAsync(userId, photos);

        Assert.Equal(200, Status(result));
        var row = await ReadAsync(userId);
        Assert.Equal($"bakhli/user_{userId}/profile/x", row.ProfilePhotoPath);
        Assert.NotNull(row.ProfilePhotoUrl);
        Assert.NotNull(row.ProfilePhotoUpdatedAt);
        Assert.Empty(photos.Deleted);
    }

    [IntegrationFact]
    public async Task Parallel_uploads_leave_one_photo_and_delete_every_replaced_file()
    {
        await using var c = CreateContext();
        var userId = await OwnerSeed.UserAsync(c, UserRoles.SeekerId);
        var photos = new CountingPhotoService();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => UploadAsync(userId, photos)));

        Assert.All(results, r => Assert.Equal(200, Status(r)));
        var row = await ReadAsync(userId);
        var deleted = photos.Inner.Deleted.ToHashSet();
        Assert.Equal(4, deleted.Count);
        Assert.DoesNotContain(row.ProfilePhotoPath!, deleted);
        Assert.Equal(photos.Saved.ToHashSet(), deleted.Append(row.ProfilePhotoPath!).ToHashSet());
    }

    [IntegrationFact]
    public async Task A_name_save_after_a_photo_save_keeps_the_photo()
    {
        await using var c = CreateContext();
        var userId = await OwnerSeed.UserAsync(c, UserRoles.SeekerId);
        await using var stale = CreateContext();
        var tracked = await stale.Users.SingleAsync(u => u.Id == userId);

        Assert.Equal(200, Status(await UploadAsync(userId, new RecordingPhotoService())));
        tracked.Name = "New name";
        tracked.UpdatedAt = DateTime.UtcNow;
        await stale.SaveChangesAsync();

        var row = await ReadAsync(userId);
        Assert.Equal("New name", row.Name);
        Assert.NotNull(row.ProfilePhotoPath);
    }

    [IntegrationFact]
    public async Task Upload_for_a_missing_user_deletes_the_new_file()
    {
        var photos = new RecordingPhotoService();
        var result = await UploadAsync(Guid.NewGuid(), photos);
        Assert.Equal(404, Status(result));
        Assert.Equal(photos.Saved.ToArray(), photos.Deleted.ToArray());
    }

    [IntegrationFact]
    public async Task Admin_removal_clears_the_columns_writes_the_notice_and_second_call_is_404()
    {
        await using var c = CreateContext();
        var userId = await OwnerSeed.UserAsync(c, UserRoles.SeekerId);
        Assert.Equal(200, Status(await UploadAsync(userId, new RecordingPhotoService())));
        var photos = new RecordingPhotoService();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        await using (var a = CreateContext())
            Assert.Equal(204, Status(await AdminUserPhotoHandlers.RemoveProfilePhoto(userId, http, new UnitOfWork(a), a, photos,
                new ServiceCollection().BuildServiceProvider(), new NullHubContext(), new RecordingPublisher())));

        var row = await ReadAsync(userId);
        Assert.Null(row.ProfilePhotoUrl);
        Assert.Null(row.ProfilePhotoPath);
        Assert.NotNull(row.ProfilePhotoUpdatedAt);
        Assert.Equal([$"bakhli/user_{userId}/profile/x"], photos.Deleted.ToArray());
        await using (var check = CreateContext())
            Assert.True(await check.NotificationEvents.AnyAsync(n => n.TargetUserId == userId && n.Type == NotificationTypes.ProfilePhotoRemoved
                && n.ActionRoute == UserNotificationRoutes.ProfilePhoto));

        await using var b = CreateContext();
        Assert.Equal(404, Status(await AdminUserPhotoHandlers.RemoveProfilePhoto(userId, http, new UnitOfWork(b), b, photos,
            new ServiceCollection().BuildServiceProvider(), new NullHubContext(), new RecordingPublisher())));
    }

    [IntegrationFact]
    public async Task Check_constraint_refuses_a_url_without_a_path()
    {
        await using var c = CreateContext();
        var userId = await OwnerSeed.UserAsync(c, UserRoles.SeekerId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => c.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"ProfilePhotoUrl\" = 'https://x' WHERE \"Id\" = {0}", userId));
        Assert.Equal("ck_users_profilephoto_pair", ex.ConstraintName);
    }

    // Statement counts of the buddy list / requests / trip members / viewer / chat list at 3 and 30 rows are pinned by the
    // existing query-count tests of those readers (same statements, one more column each); see ProfilePhotoGuardTests for
    // the source pin that no reader gained a Users join.
}

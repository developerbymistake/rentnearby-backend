using System.Collections.Concurrent;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Support;

// Hand-written IPhotoService: records saved and deleted paths, or fails the save like a Cloudinary outage.
public sealed class RecordingPhotoService(Exception? throwOnSave = null) : IPhotoService
{
    private int _counter;

    public ConcurrentQueue<string> Saved { get; } = new();
    public ConcurrentQueue<string> Deleted { get; } = new();
    public ConcurrentQueue<(Guid UserId, Guid ListingId)> ListingPrefixDeletes { get; } = new();
    public ConcurrentQueue<Guid> UserPrefixDeletes { get; } = new();

    public Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
    {
        if (throwOnSave != null) throw throwOnSave;
        var n = Interlocked.Increment(ref _counter);
        var path = $"bakhli/user_{userId}/listing_{listingId}/photo_{n}";
        Saved.Enqueue(path);
        return Task.FromResult(($"https://res.cloudinary.com/test/image/upload/{path}.jpg", path));
    }

    public Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId)
    {
        if (throwOnSave != null) throw throwOnSave;
        var n = Interlocked.Increment(ref _counter);
        var path = $"bakhli/banners/district_{districtId}/banner_{n}";
        Saved.Enqueue(path);
        return Task.FromResult(($"https://res.cloudinary.com/test/image/upload/{path}.jpg", path));
    }

    public Task DeletePhotoAsync(string filePath)
    {
        Deleted.Enqueue(filePath);
        return Task.CompletedTask;
    }

    public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId)
    {
        ListingPrefixDeletes.Enqueue((userId, listingId));
        return Task.CompletedTask;
    }

    public Task DeleteAllUserPhotosAsync(Guid userId)
    {
        UserPrefixDeletes.Enqueue(userId);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync() => Task.FromResult(true);
}

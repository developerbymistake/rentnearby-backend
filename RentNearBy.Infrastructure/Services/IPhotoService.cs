namespace RentNearBy.Infrastructure.Services;

public interface IPhotoService
{
    Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId);
    Task DeletePhotoAsync(string filePath);
    Task DeleteRoomPhotosAsync(Guid userId, Guid listingId);
    Task DeleteAllUserPhotosAsync(Guid userId);
    Task<bool> PingAsync();
}

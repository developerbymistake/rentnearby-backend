namespace RentNearBy.Infrastructure.Services;

public interface IPhotoService
{
    Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId);
    Task DeletePhotoAsync(string filePath);
    Task DeleteListingPhotosAsync(Guid userId, Guid listingId);
    Task<bool> PingAsync();
}

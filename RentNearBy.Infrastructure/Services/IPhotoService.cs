namespace RentNearBy.Infrastructure.Services;

public interface IPhotoService
{
    Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId);
    Task<(string url, string filePath)> SaveBannerAsync(Stream photoStream, string fileName, Guid districtId);
    Task<(string url, string filePath)> SaveServiceCoverPhotoAsync(Stream photoStream, string fileName, Guid serviceId);
    Task<(string url, string filePath)> SavePackageThumbnailAsync(Stream photoStream, string fileName, Guid packageId);
    Task<(string url, string filePath)> SaveAgentPhotoAsync(Stream photoStream, string fileName, Guid agentId);
    Task DeletePhotoAsync(string filePath);
    Task DeleteRoomPhotosAsync(Guid userId, Guid listingId);
    Task DeleteAllUserPhotosAsync(Guid userId);
    Task<bool> PingAsync();
}

using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RentNearBy.Infrastructure.Services;

public class PhotoService(IConfiguration configuration) : IPhotoService
{
    private readonly string _uploadBasePath = configuration["Storage:UploadPath"]
        ?? throw new InvalidOperationException("Storage:UploadPath not configured");

    private readonly string _baseUrl = configuration["Storage:BaseUrl"]
        ?? throw new InvalidOperationException("Storage:BaseUrl not configured");

    public async Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
    {
        var folder = Path.Combine(_uploadBasePath, $"user_{userId}", $"listing_{listingId}");
        Directory.CreateDirectory(folder);

        var uniqueFileName = $"{Guid.NewGuid()}.jpg";
        var filePath = Path.Combine(folder, uniqueFileName);

        using var image = await Image.LoadAsync(photoStream);

        // Resize if wider than 1200px to save storage and bandwidth
        if (image.Width > 1200)
            image.Mutate(x => x.Resize(1200, 0));

        await image.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 80 });

        var url = $"{_baseUrl}/uploads/user_{userId}/listing_{listingId}/{uniqueFileName}";
        return (url, filePath);
    }

    public Task DeletePhotoAsync(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    public Task DeleteListingPhotosAsync(Guid userId, Guid listingId)
    {
        var folder = Path.Combine(_uploadBasePath, $"user_{userId}", $"listing_{listingId}");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }
}

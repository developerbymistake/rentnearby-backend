using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RentNearBy.Infrastructure.Services;

public class PhotoService : IPhotoService
{
    private const string UploadBasePath = "/app/wwwroot/uploads";

    public async Task<(string url, string filePath)> SavePhotoAsync(Stream photoStream, string fileName, Guid userId, Guid listingId)
    {
        var folder = Path.Combine(UploadBasePath, $"user_{userId}", $"listing_{listingId}");
        Directory.CreateDirectory(folder);

        var uniqueFileName = $"{Guid.NewGuid()}.jpg";
        var filePath = Path.Combine(folder, uniqueFileName);

        using var image = await Image.LoadAsync(photoStream);

        if (image.Width > 1200)
            image.Mutate(x => x.Resize(1200, 0));

        await image.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 80 });

        var url = $"/uploads/user_{userId}/listing_{listingId}/{uniqueFileName}";
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
        var folder = Path.Combine(UploadBasePath, $"user_{userId}", $"listing_{listingId}");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }
}

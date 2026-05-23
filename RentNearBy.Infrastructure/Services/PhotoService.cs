using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RentNearBy.Infrastructure.Services;

public class PhotoService : IPhotoService
{
    private readonly Cloudinary _cloudinary;
    private const int MaxDimension = 1280;
    private const int OutputSizeLimit = 300 * 1024; // 300 KB

    public PhotoService(IConfiguration configuration)
    {
        var cloudName = configuration["CLOUDINARY_CLOUD_NAME"] ?? configuration["Cloudinary:CloudName"]
            ?? throw new InvalidOperationException("CLOUDINARY_CLOUD_NAME is not configured.");
        var apiKey = configuration["CLOUDINARY_API_KEY"] ?? configuration["Cloudinary:ApiKey"]
            ?? throw new InvalidOperationException("CLOUDINARY_API_KEY is not configured.");
        var apiSecret = configuration["CLOUDINARY_API_SECRET"] ?? configuration["Cloudinary:ApiSecret"]
            ?? throw new InvalidOperationException("CLOUDINARY_API_SECRET is not configured.");

        _cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret));
        _cloudinary.Api.Secure = true;
    }

    public async Task<(string url, string filePath)> SavePhotoAsync(
        Stream photoStream, string fileName, Guid userId, Guid listingId)
    {
        using var processed = await ProcessImageAsync(photoStream);
        var publicId = $"bakhli/user_{userId}/listing_{listingId}/{Guid.NewGuid()}";

        var result = await _cloudinary.UploadAsync(new ImageUploadParams
        {
            File = new FileDescription(fileName, processed),
            PublicId = publicId,
            Overwrite = false,
        });

        if (result.Error != null)
            throw new InvalidOperationException($"Cloudinary upload failed: {result.Error.Message}");

        return (result.SecureUrl.ToString(), publicId);
    }

    public async Task DeletePhotoAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        await _cloudinary.DestroyAsync(new DeletionParams(filePath));
    }

    public async Task DeleteListingPhotosAsync(Guid userId, Guid listingId)
    {
        await _cloudinary.DeleteResourcesByPrefixAsync($"bakhli/user_{userId}/listing_{listingId}/");
    }

    public async Task DeleteAllUserPhotosAsync(Guid userId)
    {
        await _cloudinary.DeleteResourcesByPrefixAsync($"bakhli/user_{userId}/");
    }

    public async Task<bool> PingAsync()
    {
        try
        {
            var result = await _cloudinary.PingAsync();
            return result.Error == null;
        }
        catch
        {
            return false;
        }
    }

    // --- Image processing pipeline ---
    // 1. Resize so neither dimension exceeds 1280 px (preserves aspect ratio)
    // 2. Strip EXIF / IPTC / XMP metadata (privacy + size)
    // 3. Encode to JPEG at q=82; reduce quality iteratively if output > 300 KB
    private static async Task<MemoryStream> ProcessImageAsync(Stream input)
    {
        using var image = await Image.LoadAsync(input);

        if (image.Width > MaxDimension || image.Height > MaxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(MaxDimension, MaxDimension),
                Mode = ResizeMode.Max,
            }));
        }

        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 82 });

        if (output.Length > OutputSizeLimit)
        {
            for (var quality = 70; quality >= 40; quality -= 10)
            {
                output.SetLength(0);
                output.Position = 0;
                await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = quality });
                if (output.Length <= OutputSizeLimit) break;
            }
        }

        output.Position = 0;
        return output;
    }
}

using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RentNearBy.Infrastructure.Services;

public class PhotoService : IPhotoService
{
    private readonly Cloudinary _cloudinary;
    private const int MaxDimension = 1280;
    private const int ProfileDimension = 640;
    private const int OutputSizeLimit = 300 * 1024; // 300 KB
    public const long MaxPixels = 40_000_000;

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

    public async Task<(string url, string filePath)> SaveBannerAsync(
        Stream photoStream, string fileName, Guid districtId)
    {
        using var processed = await ProcessImageAsync(photoStream);
        var publicId = $"bakhli/banners/district_{districtId}/{Guid.NewGuid()}";

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

    public async Task<(string url, string filePath)> SaveProfilePhotoAsync(
        Stream photoStream, string fileName, Guid userId)
    {
        using var processed = await ProcessImageAsync(photoStream, ProfileDimension, square: true);
        var publicId = $"bakhli/user_{userId}/profile/{Guid.NewGuid()}";

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
        await _cloudinary.DestroyAsync(new DeletionParams(filePath) { Invalidate = true });
    }

    public async Task DeleteRoomPhotosAsync(Guid userId, Guid listingId)
    {
        await _cloudinary.DeleteResourcesAsync(new DelResParams { Prefix = $"bakhli/user_{userId}/listing_{listingId}/", Invalidate = true });
    }

    public async Task DeleteAllUserPhotosAsync(Guid userId)
    {
        await _cloudinary.DeleteResourcesAsync(new DelResParams { Prefix = $"bakhli/user_{userId}/", Invalidate = true });
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

    // --- Image processing pipeline (stay, taxi, banner, profile) ---
    // Identify + 40 MP cap, first frame only, auto-orient, resize (long edge, or centre square), flatten onto white,
    // strip EXIF / IPTC / XMP, JPEG q=82 then 70..40 until <= 300 KB.
    // A broken body or a huge declared size is refused before the full decode (InvalidPhotoException, answered 400).
    internal static async Task<MemoryStream> ProcessImageAsync(Stream input, int maxDimension = MaxDimension, bool square = false)
    {
        var start = input.CanSeek ? input.Position : 0;
        Image image;
        try
        {
            var info = await Image.IdentifyAsync(input);
            if (info == null || info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
                throw new InvalidPhotoException();
            input.Position = start;
            image = await Image.LoadAsync(new DecoderOptions { MaxFrames = 1 }, input);
        }
        catch (ImageFormatException ex)
        {
            throw new InvalidPhotoException(ex);
        }
        using var _ = image;

        image.Mutate(x => x.AutoOrient());
        if (square)
        {
            var side = Math.Min(maxDimension, Math.Min(image.Width, image.Height));
            if (image.Width != side || image.Height != side)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(side, side),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                }));
            }
        }
        else if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(maxDimension, maxDimension),
                Mode = ResizeMode.Max,
            }));
        }
        image.Mutate(x => x.BackgroundColor(Color.White));

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

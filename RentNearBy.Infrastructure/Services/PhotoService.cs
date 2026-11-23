using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace RentNearBy.Infrastructure.Services;

public class PhotoService : IPhotoService
{
    private readonly Cloudinary _cloudinary;
    private const int MaxDimension = 1280;
    private const int ProfileDimension = 640;
    private const int OutputSizeLimit = 300 * 1024; // 300 KB
    public const long MaxPixels = 40_000_000;
    // The stream is always the re-encoded JPEG; the client file name is never sent.
    private const string UploadFileName = "photo.jpg";

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
            File = new FileDescription(UploadFileName, processed),
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
            File = new FileDescription(UploadFileName, processed),
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
            File = new FileDescription(UploadFileName, processed),
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
        var result = await _cloudinary.DestroyAsync(new DeletionParams(filePath) { Invalidate = true });
        // "ok" and "not found" both mean the file is gone; a refused call is an Error.
        if (result.Error != null)
            throw new InvalidOperationException($"Cloudinary delete failed: {result.Error.Message}");
    }

    public Task DeleteRoomPhotosAsync(Guid userId, Guid listingId)
        => DeletePrefixAsync($"bakhli/user_{userId}/listing_{listingId}/");

    public Task DeleteAllUserPhotosAsync(Guid userId)
        => DeletePrefixAsync($"bakhli/user_{userId}/");

    // One call deletes one page; follow the cursor until Cloudinary says nothing is left.
    private async Task DeletePrefixAsync(string prefix)
    {
        string? cursor = null;
        for (var page = 0; page < MaxDeletePages; page++)
        {
            var result = await _cloudinary.DeleteResourcesAsync(new DelResParams { Prefix = prefix, Invalidate = true, NextCursor = cursor });
            if (result.Error != null)
                throw new InvalidOperationException($"Cloudinary delete failed: {result.Error.Message}");
            if (!result.Partial || string.IsNullOrEmpty(result.NextCursor)) return;
            cursor = result.NextCursor;
        }
        throw new InvalidOperationException("Cloudinary delete failed: too many pages");
    }

    private const int MaxDeletePages = 50;

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

    // Only the three accepted formats can be decoded: the TIFF/BMP/GIF/TGA/PBM/QOI codecs (and their advisories) are unreachable.
    internal static readonly Configuration DecodeConfiguration = new(
        new JpegConfigurationModule(), new PngConfigurationModule(), new WebpConfigurationModule());

    private static readonly DecoderOptions Decoder = new() { Configuration = DecodeConfiguration, MaxFrames = 1 };

    // At most two full decodes at once (about 160 MB per 40 MP image); the Cloudinary upload stays outside the gate.
    internal const int DecodePermits = 2;
    private static readonly SemaphoreSlim Decode = new(DecodePermits, DecodePermits);
    internal static int FreeDecodeSlots => Decode.CurrentCount;

    private readonly struct DecodeRelease : IDisposable
    {
        public void Dispose() => Decode.Release();
    }

    // --- Image processing pipeline (stay, taxi, banner, profile) ---
    // Identify + 40 MP cap, first frame only, auto-orient, resize (long edge, or centre square), flatten onto white,
    // strip EXIF / IPTC / XMP, JPEG q=82 then 70..40 until <= 300 KB.
    // A broken body, any decoder failure or a huge declared size is refused (InvalidPhotoException, answered 400).
    internal static async Task<MemoryStream> ProcessImageAsync(Stream input, int maxDimension = MaxDimension, bool square = false)
    {
        await Decode.WaitAsync();
        using var gate = new DecodeRelease();
        var start = input.CanSeek ? input.Position : 0;
        Image image;
        try
        {
            var info = await Image.IdentifyAsync(Decoder, input);
            if (info == null || info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
                throw new InvalidPhotoException();
            input.Position = start;
            image = await Image.LoadAsync(Decoder, input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not InvalidPhotoException)
        {
            // A decoder failure on uploaded bytes (incl. NullReferenceException of an empty VP8X WebP) is an invalid photo.
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

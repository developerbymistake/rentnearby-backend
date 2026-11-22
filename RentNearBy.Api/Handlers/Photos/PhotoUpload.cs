using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Photos;

// The one upload check of every photo handler (size, then JPEG / PNG / WebP bytes). Null = ok.
public static class PhotoUpload
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string NotImage = "File must be a valid image (JPEG, PNG or WebP)";
    public const string TooBig = "Photo size must not exceed 10MB";

    public static IResult? Check(IFormFile? file, string notImageMessage = NotImage, string tooBigMessage = TooBig)
    {
        if (file == null || file.Length == 0) return BadRequestResponse(notImageMessage);
        if (file.Length > MaxBytes) return BadRequestResponse(tooBigMessage);
        using var stream = file.OpenReadStream();
        return IsImage(stream) ? null : BadRequestResponse(notImageMessage);
    }

    public static bool IsImage(Stream stream)
    {
        var start = stream.CanSeek ? stream.Position : 0;
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var n = stream.Read(header, read, header.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (stream.CanSeek) stream.Position = start;
        if (read < 3) return false;
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
        if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        return false;
    }
}

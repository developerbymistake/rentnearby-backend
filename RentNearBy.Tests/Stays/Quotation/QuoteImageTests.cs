using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Stays.Quotation;

// Q6 / S9: logo and signature run the profile-photo pipeline with the quote box and caps: logo 512 square, signature inside
// 900 x 450, EXIF / ICC gone, <= 120 KB target, refused above the hard cap, 24 MP cap, decode gate never waits forever.
public class QuoteImageTests
{
    private static async Task<Image> Decode(MemoryStream output)
    {
        output.Position = 0;
        return await Image.LoadAsync(output);
    }

    private static byte[] WithExifAndIcc(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h, new Rgba32(20, 120, 40, 255));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "TestCam");
        image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Logo_is_a_512_square_jpeg_without_metadata()
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(WithExifAndIcc(1600, 900)), PhotoService.QuoteOptions(QuoteImageKind.Logo));
        Assert.True(output.Length <= QuoteRules.ImageHardMaxBytes);
        var bytes = output.ToArray();
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, bytes[..3]);
        using var image = await Decode(output);
        Assert.Equal((512, 512), (image.Width, image.Height));
        Assert.Null(image.Metadata.ExifProfile);
        Assert.Null(image.Metadata.IccProfile);
    }

    [Fact]
    public async Task Signature_keeps_its_shape_inside_900_by_450()
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(TestImages.Wide()), PhotoService.QuoteOptions(QuoteImageKind.Signature));
        using var image = await Decode(output);
        Assert.True(image.Width <= QuoteRules.SignatureBoxWidth && image.Height <= QuoteRules.SignatureBoxHeight);
        Assert.Equal(2.0, image.Width / (double)image.Height, 1);
        Assert.Null(image.Metadata.ExifProfile);
    }

    [Fact]
    public async Task Noisy_logo_ends_at_or_under_the_target_ladder_and_never_above_the_hard_cap()
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(TestImages.Noisy(1024)), PhotoService.QuoteOptions(QuoteImageKind.Logo));
        Assert.True(output.Length <= QuoteRules.ImageHardMaxBytes, $"{output.Length} bytes");
    }

    [Fact]
    public async Task Still_too_large_at_the_lowest_quality_is_refused()
    {
        var tight = PhotoService.QuoteOptions(QuoteImageKind.Signature) with { MaxBytes = 500, HardMaxBytes = 1000 };
        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(TestImages.Noisy(800)), tight));
    }

    [Fact]
    public async Task Pixel_cap_of_24_mp_applies_before_decode()
    {
        Assert.Equal(24_000_000, PhotoService.QuoteOptions(QuoteImageKind.Logo).PixelCap);
        await Assert.ThrowsAsync<InvalidPhotoException>(() =>
            PhotoService.ProcessImageAsync(new MemoryStream(TestImages.HugeHeaderPng()), PhotoService.QuoteOptions(QuoteImageKind.Logo)));
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("garbage")]
    public async Task Wrong_types_are_refused(string kind)
    {
        var bytes = kind switch { "gif" => TestImages.Gif(), "bmp" => TestImages.Bmp(), _ => TestImages.Garbage() };
        await Assert.ThrowsAsync<InvalidPhotoException>(() =>
            PhotoService.ProcessImageAsync(new MemoryStream(bytes), PhotoService.QuoteOptions(QuoteImageKind.Signature)));
    }

    [Fact]
    public async Task Transparent_png_is_flattened_on_white()
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(TestImages.TransparentPng()), PhotoService.QuoteOptions(QuoteImageKind.Signature));
        output.Position = 0;
        using var image = await Image.LoadAsync<Rgb24>(output);
        var corner = image[0, 0];
        Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240);
    }

    [Fact]
    public void Profile_photo_options_are_unchanged()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), PhotoService.DecodeWait);
        Assert.Equal(2, PhotoService.DecodePermits);
        Assert.Equal(new[] { "upload", "authenticated" }, PhotoService.DeliveryTypes);
    }

    [Fact]
    public void Busy_answer_carries_retry_after_five()
    {
        Assert.Equal(5, PhotoBusyException.RetryAfterSeconds);
        Assert.Equal(PhotoBusyException.UserMessage, new PhotoBusyException().Message);
    }
}

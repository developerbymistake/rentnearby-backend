using Microsoft.EntityFrameworkCore;
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

    // I-3: a Cloudinary outage on the image read (transport error, HTTP error status, download timeout) is the typed PhotoBusy
    // (503 + Retry-After in the handler), never a 500; a cancel of the caller's own request is not turned into an outage.
    private sealed class FailingReadStore(Exception failure) : RentNearBy.Infrastructure.Services.Owner.Quote.IQuoteImageStore
    {
        public Task<string> SaveQuoteImageAsync(Stream input, Guid userId, QuoteImageKind kind) => throw new NotSupportedException();
        public Task<byte[]?> ReadQuoteImageAsync(string publicId, CancellationToken ct) => Task.FromException<byte[]?>(failure);
        public Task DeleteQuoteImageAsync(string publicId) => Task.CompletedTask;
        public Task DeleteAllQuoteImagesAsync(Guid userId) => Task.CompletedTask;
    }

    private static RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteImageService ImagesOver(
        RentNearBy.Infrastructure.Services.Owner.Quote.IQuoteImageStore store, RentNearBy.Infrastructure.Data.ApplicationDbContext db)
        => new(db, store, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteImageService>.Instance);

    private static RentNearBy.Infrastructure.Data.ApplicationDbContext DesignContext() => new(
        new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RentNearBy.Infrastructure.Data.ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task A_store_outage_on_read_is_photo_busy(string kind)
    {
        Exception failure = kind == "http"
            ? new HttpRequestException("503 from cloudinary", null, System.Net.HttpStatusCode.ServiceUnavailable)
            : new TaskCanceledException("download timed out", new TimeoutException());
        await using var db = DesignContext();
        var result = await ImagesOver(new FailingReadStore(failure), db).ReadStoredAsync($"bakhli/user_{Guid.NewGuid()}/quote/logo/x", CancellationToken.None);
        Assert.Equal(QuoteErrors.PhotoBusy, result.ErrorType);
        Assert.Equal(503, QuoteErrors.StatusOf(QuoteErrors.PhotoBusy));
    }

    [Fact]
    public async Task A_cancelled_request_and_a_bad_stored_file_are_not_an_outage()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteImageService.IsStoreOutage(new TaskCanceledException(), cts.Token));
        Assert.True(RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteImageService.IsStoreOutage(new TaskCanceledException(), CancellationToken.None));
        Assert.False(RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteImageService.IsStoreOutage(new InvalidOperationException(), CancellationToken.None));

        await using var db = DesignContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImagesOver(new FailingReadStore(new InvalidOperationException("too large")), db)
            .ReadStoredAsync("p", CancellationToken.None));
    }

    [Fact]
    public void Image_read_refusal_sets_retry_after()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Api", "Handlers", "Owner", "StayQuotationHandlers.cs"));
        Assert.Contains("if (result.ErrorType == QuoteErrors.PhotoBusy) http.Response.Headers.RetryAfter = PhotoBusyException.RetryAfterSeconds.ToString();", src);
    }
}

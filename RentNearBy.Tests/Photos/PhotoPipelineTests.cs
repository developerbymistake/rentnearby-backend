using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace RentNearBy.Tests.Photos;

// CONTRACT_BACKEND_DP test 7.1 / COMPRESSION_SWEEP G1, G8, transparency: one pipeline for stay, taxi, banner and profile.
public class PhotoPipelineTests
{
    private static async Task<Image<Rgba32>> RunAsync(byte[] input, int max = 1280, bool square = false)
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(input), max, square);
        Assert.Equal(JpegFormat.Instance, Image.DetectFormat(output.ToArray()));
        output.Position = 0;
        return await Image.LoadAsync<Rgba32>(output);
    }

    [Fact]
    public async Task Exif_orientation_6_comes_out_upright_without_exif()
    {
        using var image = await RunAsync(TestImages.PortraitExif6());
        Assert.Equal(300, image.Width);
        Assert.Equal(400, image.Height);
        Assert.Null(image.Metadata.ExifProfile);
        Assert.Null(image.Metadata.XmpProfile);
        Assert.Null(image.Metadata.IptcProfile);
    }

    [Fact]
    public async Task Animated_webp_keeps_only_the_first_frame()
    {
        var input = TestImages.AnimatedWebp();
        using (var fixture = Image.Load(input)) Assert.Equal(3, fixture.Frames.Count);

        using var image = await RunAsync(input);
        Assert.Equal(1, image.Frames.Count);
        var centre = image[32, 32];
        Assert.True(centre.R > 200 && centre.G < 60 && centre.B < 60, centre.ToString());
    }

    [Fact]
    public async Task Transparent_png_is_flattened_onto_white()
    {
        using var image = await RunAsync(TestImages.TransparentPng());
        var corner = image[2, 2];
        Assert.True(corner.R >= 245 && corner.G >= 245 && corner.B >= 245, corner.ToString());
        var centre = image[100, 100];
        Assert.True(centre.B > 200 && centre.R < 60, centre.ToString());
    }

    [Theory]
    [InlineData("wide", 1280, 640)]
    [InlineData("tall", 640, 1280)]
    public async Task Listing_mode_caps_the_long_edge_at_1280_and_keeps_the_ratio(string kind, int w, int h)
    {
        using var image = await RunAsync(kind == "wide" ? TestImages.Wide() : TestImages.Tall());
        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);
    }

    [Fact]
    public async Task Listing_mode_never_upscales()
    {
        using var image = await RunAsync(TestImages.Small());
        Assert.Equal(300, image.Width);
        Assert.Equal(200, image.Height);
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("tall")]
    public async Task Square_mode_crops_to_640(string kind)
    {
        using var image = await RunAsync(kind == "wide" ? TestImages.Wide() : TestImages.Tall(), 640, square: true);
        Assert.Equal(640, image.Width);
        Assert.Equal(640, image.Height);
    }

    [Fact]
    public async Task Square_mode_small_image_is_not_upscaled()
    {
        using var image = await RunAsync(TestImages.Small(), 640, square: true);
        Assert.Equal(200, image.Width);
        Assert.Equal(200, image.Height);
    }

    [Fact]
    public async Task Square_mode_orients_before_cropping()
    {
        using var image = await RunAsync(TestImages.PortraitExif6(), 640, square: true);
        Assert.Equal(300, image.Width);
        Assert.Equal(300, image.Height);
    }

    [Fact]
    public async Task Huge_declared_size_is_refused_before_decode()
    {
        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(TestImages.HugeHeaderPng())));
    }

    [Theory]
    [InlineData("tiff")]
    [InlineData("gif")]
    [InlineData("bmp")]
    public async Task Formats_outside_jpeg_png_webp_are_never_decoded(string format)
    {
        using var source = new Image<Rgba32>(16, 16);
        var bytes = new MemoryStream();
        if (format == "tiff") source.SaveAsTiff(bytes);
        else if (format == "gif") source.SaveAsGif(bytes);
        else source.SaveAsBmp(bytes);

        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(bytes.ToArray())));
    }

    [Fact]
    public async Task Garbage_bytes_are_refused()
    {
        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(TestImages.Garbage())));
    }

    [Fact]
    public async Task Noisy_image_ends_under_300_KB_or_at_quality_40()
    {
        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(TestImages.Noisy()));
        if (output.Length <= 300 * 1024) return;
        using var image = await Image.LoadAsync(output);
        Assert.InRange(image.Metadata.GetJpegMetadata().Quality, 1, 45);
    }
    [Theory]
    [InlineData("broken")]
    [InlineData("vp8x")]
    public async Task Damaged_webp_is_an_invalid_photo_not_a_server_error(string kind)
    {
        var bytes = kind == "broken" ? TestImages.BrokenWebp() : TestImages.EmptyVp8xWebp();
        await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Never_more_than_two_decodes_at_once()
    {
        Assert.Equal(2, PhotoService.DecodePermits);
        var big = TestImages.Noisy(2400);
        var lowest = PhotoService.DecodePermits;
        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var output = await PhotoService.ProcessImageAsync(new MemoryStream(big));
            return output.Length;
        })).ToArray();
        var all = Task.WhenAll(runs);
        while (!all.IsCompleted)
        {
            lowest = Math.Min(lowest, PhotoService.FreeDecodeSlots);
            Assert.InRange(PhotoService.FreeDecodeSlots, 0, PhotoService.DecodePermits);
            await Task.Delay(1);
        }
        await all;
        Assert.Equal(0, lowest);
        Assert.All(runs, r => Assert.True(r.Result <= 300 * 1024));
    }

    [Fact]
    public async Task A_refused_file_gives_its_decode_slot_back()
    {
        for (var i = 0; i < PhotoService.DecodePermits + 3; i++)
            await Assert.ThrowsAsync<InvalidPhotoException>(() => PhotoService.ProcessImageAsync(new MemoryStream(TestImages.EmptyVp8xWebp())));

        using var output = await PhotoService.ProcessImageAsync(new MemoryStream(TestImages.SmallJpeg()));
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void The_gate_wraps_the_decode_and_not_the_upload()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "PhotoService.cs"));
        var body = src[src.IndexOf("ProcessImageAsync(Stream input, ImageProcessOptions options)", StringComparison.Ordinal)..];
        var gate = body.IndexOf("await Decode.WaitAsync(DecodeWait)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < body.IndexOf("Image.IdentifyAsync", StringComparison.Ordinal));
        Assert.Contains("using var gate = new DecodeRelease();", body);
        Assert.DoesNotContain("_cloudinary", body);
    }
}

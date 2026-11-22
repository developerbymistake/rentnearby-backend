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
}

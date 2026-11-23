using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers.Photos;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Tests.Support;

namespace RentNearBy.Tests.Photos;

// COMPRESSION_SWEEP D2, D4, D5: one check before every save (size, then JPEG / PNG / WebP bytes).
public class PhotoUploadCheckTests
{
    private static IFormFile File(byte[] bytes, long? length = null)
        => new FormFile(new MemoryStream(bytes), 0, length ?? bytes.Length, "photo", "x.jpg");

    private static (int Status, string Message) Refusal(IResult? result)
    {
        Assert.NotNull(result);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        return (status, body.Error!.Message);
    }

    [Fact]
    public void Null_and_empty_files_answer_the_not_image_text()
    {
        Assert.Equal((400, PhotoUpload.NotImage), Refusal(PhotoUpload.Check(null)));
        Assert.Equal((400, PhotoUpload.NotImage), Refusal(PhotoUpload.Check(File(TestImages.Empty()))));
    }

    [Fact]
    public void Over_10_MB_answers_the_too_big_text_without_reading()
    {
        Assert.Equal((400, PhotoUpload.TooBig), Refusal(PhotoUpload.Check(File(TestImages.SmallJpeg(), PhotoUpload.MaxBytes + 1))));
    }

    [Fact]
    public void Exactly_10_MB_is_allowed_by_size()
    {
        var bytes = new byte[PhotoUpload.MaxBytes];
        TestImages.SmallJpeg().CopyTo(bytes, 0);
        Assert.Null(PhotoUpload.Check(File(bytes)));
    }

    [Fact]
    public void Jpeg_png_and_webp_pass()
    {
        Assert.Null(PhotoUpload.Check(File(TestImages.SmallJpeg())));
        Assert.Null(PhotoUpload.Check(File(TestImages.Png())));
        Assert.Null(PhotoUpload.Check(File(TestImages.Webp())));
    }

    [Fact]
    public void Gif_bmp_heic_text_and_two_bytes_are_refused()
    {
        foreach (var bytes in new[] { TestImages.Gif(), TestImages.Bmp(), TestImages.Heic(), "hello world, not a photo"u8.ToArray(), [0xFF, 0xD8], TestImages.Garbage() })
            Assert.Equal((400, PhotoUpload.NotImage), Refusal(PhotoUpload.Check(File(bytes))));
    }

    [Fact]
    public void Png_needs_the_full_8_byte_signature()
    {
        byte[] fake = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal((400, PhotoUpload.NotImage), Refusal(PhotoUpload.Check(File(fake))));
        Assert.Null(PhotoUpload.Check(File(TestImages.Png())));
        Assert.Null(PhotoUpload.Check(File(TestImages.TransparentPng())));
    }

    [Fact]
    public void Passed_in_texts_are_returned_verbatim()
    {
        Assert.Equal((400, "Not a photo"), Refusal(PhotoUpload.Check(null, "Not a photo", "Too big")));
        Assert.Equal((400, "Not a photo"), Refusal(PhotoUpload.Check(File(TestImages.Bmp()), "Not a photo", "Too big")));
        Assert.Equal((400, "Too big"), Refusal(PhotoUpload.Check(File(TestImages.SmallJpeg(), PhotoUpload.MaxBytes + 1), "Not a photo", "Too big")));
    }

    [Fact]
    public void The_file_reads_from_the_start_after_the_check()
    {
        var bytes = TestImages.SmallJpeg();
        var file = File(bytes);
        Assert.Null(PhotoUpload.Check(file));
        using var stream = file.OpenReadStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public void IsImage_leaves_a_seekable_stream_where_it_was()
    {
        var stream = new MemoryStream(TestImages.Png());
        Assert.True(PhotoUpload.IsImage(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Caps_match_the_taxi_rule_and_the_old_texts()
    {
        Assert.Equal(TaxiPhotoRules.MaxBytes, PhotoUpload.MaxBytes);
        Assert.Equal("File must be a valid image (JPEG, PNG or WebP)", PhotoUpload.NotImage);
        Assert.Equal("Photo size must not exceed 10MB", PhotoUpload.TooBig);
    }
}

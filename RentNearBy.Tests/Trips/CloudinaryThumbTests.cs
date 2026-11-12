using RentNearBy.Core.Utils;

namespace RentNearBy.Tests.Trips;

public class CloudinaryThumbTests
{
    private const string T = CloudinaryThumb.CityCover;

    [Fact]
    public void Cloudinary_InsertsTransformationAfterUpload()
        => Assert.Equal($"https://res.cloudinary.com/demo/image/upload/{T}/v12/a/b.jpg",
            CloudinaryThumb.Apply("https://res.cloudinary.com/demo/image/upload/v12/a/b.jpg"));

    [Fact]
    public void Apply_IsIdempotent()
    {
        var once = CloudinaryThumb.Apply("https://res.cloudinary.com/demo/image/upload/v1/a.jpg");
        Assert.Equal(once, CloudinaryThumb.Apply(once));
    }

    [Fact]
    public void ExistingTransformation_IsChainedNotDropped()
        => Assert.Equal($"https://res.cloudinary.com/demo/image/upload/{T}/e_grayscale/v1/a.jpg",
            CloudinaryThumb.Apply("https://res.cloudinary.com/demo/image/upload/e_grayscale/v1/a.jpg"));

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a.jpg")]
    [InlineData("https://res.cloudinary.com/demo/video/upload/v1/a.mp4")]
    [InlineData("not a url")]
    public void NonCloudinaryOrNonImage_ReturnedUnchanged(string url)
        => Assert.Equal(url, CloudinaryThumb.Apply(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIsNull(string? url) => Assert.Null(CloudinaryThumb.Apply(url));

    [Fact]
    public void Url_IsTrimmed()
        => Assert.Equal("https://x.test/a.jpg", CloudinaryThumb.Apply("  https://x.test/a.jpg "));
}

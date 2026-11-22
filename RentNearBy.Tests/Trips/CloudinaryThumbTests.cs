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

    [Fact]
    public void SpotSizes_MatchTheAppTable()
    {
        Assert.Equal("c_fill,w_96,h_96,q_auto,f_auto", CloudinaryThumb.SpotMarker);
        Assert.Equal("c_fill,g_auto,w_320,h_320,q_auto,f_auto", CloudinaryThumb.SpotSquare);
        Assert.Equal("c_fill,g_auto,w_720,h_400,q_auto,f_auto", CloudinaryThumb.SpotCard);
        Assert.Equal("c_fill,g_auto,w_1080,h_640,q_auto,f_auto", CloudinaryThumb.SpotHero);
        Assert.Equal("c_limit,w_1280,q_auto,f_auto", CloudinaryThumb.SpotFull);
        Assert.Equal(
            [CloudinaryThumb.SpotMarker, CloudinaryThumb.SpotSquare, CloudinaryThumb.SpotCard, CloudinaryThumb.SpotHero, CloudinaryThumb.SpotFull],
            CloudinaryThumb.SpotSizes);
    }

    public static TheoryData<string> SpotSizeData => new(CloudinaryThumb.SpotSizes);

    [Theory]
    [MemberData(nameof(SpotSizeData))]
    public void SpotSize_InsertedAfterUpload_Idempotent_AndSafe(string size)
    {
        var url = CloudinaryThumb.Apply("https://res.cloudinary.com/demo/image/upload/v3/atlas/spots/a.jpg", size);
        Assert.Equal($"https://res.cloudinary.com/demo/image/upload/{size}/v3/atlas/spots/a.jpg", url);
        Assert.Equal(url, CloudinaryThumb.Apply(url, size));
        Assert.Equal("https://upload.wikimedia.org/a.jpg", CloudinaryThumb.Apply("https://upload.wikimedia.org/a.jpg", size));
        Assert.Null(CloudinaryThumb.Apply(null, size));
    }
}

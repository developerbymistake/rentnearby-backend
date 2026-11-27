using RentNearBy.Core.Models.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Owner 2026-11-27: Near me = the trip's first city within 150 km of the viewer.
public class NearMeRadiusTests
{
    [Fact]
    public void Near_me_radius_is_150_km_and_the_feed_query_uses_it()
    {
        Assert.Equal(150, SocialLimits.NearMeKm);
        var src = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Infrastructure", "Services", "Social", "PublicTripReader.cs"));
        Assert.Contains("SocialLimits.NearMeKm * 1000", src);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services.Owner.Be4;

namespace RentNearBy.Tests.Owner.Be4;

// AU-8: the Shown counter never throws, never waits, and the owner's own detail view is not counted; nearby feeds never count.
public class ShownCounterTests
{
    [Fact]
    public void No_redis_is_swallowed()
    {
        var counter = new OwnerShownCounter(new Be4Clock(Be4Facts.Now), NullLogger<OwnerShownCounter>.Instance);
        counter.Add(Guid.NewGuid(), Guid.NewGuid(), null);
        counter.AddListed([Guid.NewGuid(), Guid.NewGuid()]);
    }

    [Fact]
    public void Key_is_the_ist_day()
        => Assert.Equal("owner:shown:20261121", OwnerShownCounter.Key(Be4Facts.Today));

    [Fact]
    public void Only_the_trip_stay_list_the_detail_and_the_share_link_count_and_never_the_owner_own_view()
    {
        var source = StaySource();
        Assert.Equal(3, CountOf(source, "OwnerShownCounter"));
        Assert.Contains("if (viewer != stay.UserId) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddViewed(", source);
        Assert.Contains("if (!isOwner) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddViewed(", source);
        Assert.Contains("!= UserRoles.StayOwner) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddListed", source);
    }

    private static string StaySource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "RentNearBy.sln"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, "RentNearBy.Api", "Handlers", "StayHandlers.cs"));
    }

    private static int CountOf(string text, string word)
    {
        var n = 0;
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

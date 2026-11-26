using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Spec 22: every viewer gets the full post; notOpen only marks a viewer whose gender may not ask to join.
public class PublicTripVisibilityTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Viewer = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);

    private static PublicTripReader.Row Row(string joinGender) => new()
    {
        TripId = Guid.NewGuid(), OwnerUserId = Owner, OwnerName = "Riya Sharma", JoinGender = joinGender, PostText = "Easy treks, shared cab.",
        PostDays = 4, Seats = 3, Joined = 1, PublishedAt = Now.AddDays(-1), PostWindowMonths = 2, CityNames = ["Manali"], IsPublic = true, Live = true,
    };

    [Theory]
    [InlineData(JoinGenders.Women, "Male", true)]
    [InlineData(JoinGenders.Women, "Other", true)]
    [InlineData(JoinGenders.Women, null, true)]
    [InlineData(JoinGenders.Women, "Female", false)]
    [InlineData(JoinGenders.Men, "Female", true)]
    [InlineData(JoinGenders.Men, "Male", false)]
    [InlineData(JoinGenders.Anyone, "Other", false)]
    public void Other_gender_sees_the_whole_post_with_not_open(string joinGender, string? viewerGender, bool notOpen)
    {
        var dto = PublicTripReader.ToDto(Row(joinGender), Viewer, Now, null, viewerGender);
        Assert.Equal(notOpen, dto.NotOpen);
        Assert.Equal("Easy treks, shared cab.", dto.Text);
        Assert.Equal(4, dto.Days);
        Assert.Equal(3, dto.Seats);
        Assert.Equal(2, dto.SeatsLeft);
        Assert.Equal(joinGender, dto.JoinGender);
        Assert.Equal(SocialRules.WhenWithin, dto.When.Kind);
        Assert.True(dto.When.Months > 0);
    }

    [Fact]
    public void Own_post_is_never_not_open()
    {
        var dto = PublicTripReader.ToDto(Row(JoinGenders.Women), Owner, Now, null, "Male");
        Assert.False(dto.NotOpen);
        Assert.True(dto.IsMine);
    }
}

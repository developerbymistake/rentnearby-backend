using RentNearBy.Core.DTOs.Social;

namespace RentNearBy.Core.Models.Social;

// Home rail when no live post can be shown (contract 2.4): three constant Bakhli team sample trips. A tap opens the feed.
public static class SocialSamples
{
    public const string TeamName = "Bakhli team";

    public static readonly Guid ManaliId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid RishikeshId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid JaipurId = new("00000000-0000-0000-0000-000000000003");

    public static List<PublicTripDto> Rail() =>
    [
        Sample(ManaliId, "Manali", "Himachal Pradesh", 4, 2, "Snow, cafes and a short trek. Looking for two easy-going travel buddies."),
        Sample(RishikeshId, "Rishikesh", "Uttarakhand", 5, 3, "River rafting, the evening aarti and slow mornings by the Ganga."),
        Sample(JaipurId, "Jaipur", "Rajasthan", 3, 1, "Forts, old markets and street food. Happy to split a cab."),
    ];

    private static PublicTripDto Sample(Guid id, string city, string state, int days, int withinMonths, string text) => new()
    {
        TripId = id,
        Owner = new SocialPersonDto { UserId = Guid.Empty, Name = TeamName, Initial = "B" },
        CityNames = [city],
        StateName = state,
        When = new WhenDto { Kind = SocialRules.WhenWithin, Months = withinMonths },
        Days = days,
        JoinGender = JoinGenders.Anyone,
        Seats = 3,
        SeatsLeft = 3,
        Text = text,
        MyJoin = MyJoinStates.None,
        Sample = true,
    };
}

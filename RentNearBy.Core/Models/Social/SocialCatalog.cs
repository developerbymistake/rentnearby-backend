namespace RentNearBy.Core.Models.Social;

// Fixed choice lists of the social profile and the post wizard (contract 1.1).
public static class SocialCatalog
{
    public static readonly IReadOnlyList<string> Interests =
        ["Mountains", "Treks", "Beaches", "Adventure", "Food", "Culture", "Photography", "Spiritual", "Road trips", "Camping"];

    public static readonly IReadOnlyList<string> Skills =
        ["Photographer", "Driver", "Cook", "Tour guide", "Content creator", "First aid"];

    public static readonly IReadOnlyList<string> AboutExamples =
    [
        "Hills over beaches. Chai at every stop.",
        "Weekend trekker. Happy to share a cab.",
        "Slow travel, local food, early mornings.",
    ];

    public static readonly IReadOnlyList<string> PostChips =
    [
        "Budget trip", "Backpacking", "Happy to split a cab", "Staying in hostels", "First solo trip", "Into photography", "Up for treks",
    ];

    public static IReadOnlyList<string> Genders => SocialGenders.All;
}

namespace RentNearBy.Core.Models;

// Districts of these states are seeded with business onboarding ON. Exact StateName match, as in districts.json.
public static class OnboardingLaunchStates
{
    public static readonly string[] Names =
        ["Uttarakhand", "Himachal Pradesh", "Rajasthan", "Goa", "Kerala", "Jammu and Kashmir"];

    public static bool Contains(string? stateName) =>
        stateName != null && Array.IndexOf(Names, stateName) >= 0;
}

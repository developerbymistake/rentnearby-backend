namespace RentNearBy.Core.Models.Social;

// PlanFeatures keys of the Seeker plans (contract 4). Not part of PlanFeatureKeys / PlanKeyCatalog (listing keys).
public static class SeekerPlanKeys
{
    public const string ActiveTrips = "SeekerActiveTrips";
    public const string PublicTrips = "SeekerPublicTrips";
    public const string JoinRequestsPerMonth = "SeekerJoinRequestsPerMonth";
    public const string NewChatsPerMonth = "SeekerNewChatsPerMonth";
    public const string GenderOnlyTrips = "SeekerGenderOnlyTrips";
    public const string NearMe = "SeekerNearMe";

    public static readonly IReadOnlyList<string> All = [ActiveTrips, PublicTrips, JoinRequestsPerMonth, NewChatsPerMonth, GenderOnlyTrips, NearMe];

    private static readonly Dictionary<string, (int Free, int Plus)> Seeds = new()
    {
        [ActiveTrips] = (1, 10),
        [PublicTrips] = (1, 3),
        [JoinRequestsPerMonth] = (1, 5),
        [NewChatsPerMonth] = (3, 15),
        [GenderOnlyTrips] = (0, 1),
        [NearMe] = (0, 1),
    };

    public static int FreeDefault(string key) => Seeds.TryGetValue(key, out var v) ? v.Free : 0;

    public static int PlusDefault(string key) => Seeds.TryGetValue(key, out var v) ? v.Plus : 0;
}

// Seeded Seeker plans (contract 4): stable ids, Play product and prices.
public static class SeekerPlanSeed
{
    public static readonly Guid FreePlanId = new("b1000000-0000-0000-0000-000000000501");
    public static readonly Guid PlusPlanId = new("b1000000-0000-0000-0000-000000000502");
    public static readonly Guid Price1mId = new("b2000000-0000-0000-0000-000000000501");
    public static readonly Guid Price3mId = new("b2000000-0000-0000-0000-000000000503");
    public static readonly Guid Price12mId = new("b2000000-0000-0000-0000-000000000512");
    public const string FreeCode = "SEEKER_FREE";
    public const string PlusCode = "SEEKER_PLUS";
    public const string PlusName = "Bakhli Plus";
    public const string PlusTagline = "Join more trips";
    public const string PlayProductId = "seeker_plus";
    public const int Price1m = 200;
    public const int Price3m = 500;
    public const int Price12m = 1800;
}

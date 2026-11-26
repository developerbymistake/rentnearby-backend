using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Support.Social;

// Seed builders for the social integration tests (plain context).
public static class SocialSeed
{
    public static async Task ProfileAsync(ApplicationDbContext db, Guid userId, string gender = SocialGenders.Male, bool isPrivate = false,
        bool guidelines = true, string messagePolicy = MessagePolicies.Everyone, bool notifyMessages = true, bool notifyFollows = true,
        bool notifyFollowedTrips = true)
    {
        var now = DateTime.UtcNow;
        db.Set<UserProfile>().Add(new UserProfile
        {
            UserId = userId, Gender = gender, BirthMonth = 5, BirthYear = 1995, About = "Weekend trekker.",
            Interests = ["Treks", "Food", "Culture"], Skills = ["Driver"], Places = ["Manali"], IsPrivate = isPrivate,
            MessagePolicy = messagePolicy, NotifyMessages = notifyMessages, NotifyFollows = notifyFollows, NotifyFollowedTrips = notifyFollowedTrips,
            GuidelinesAcceptedAt = guidelines ? now : null, GuidelinesVersion = guidelines ? SocialLimits.GuidelinesVersion : 0,
            CompletedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }
}

// A one-city catalog: Nainital (Uttarakhand, Ready) and Almora (Uttarakhand, Ready), Manali (Himachal Pradesh, Ready).
public sealed class FakeCityCatalog : ITripCityCatalog
{
    public const long Nainital = 210000001;
    public const long Almora = 210000002;
    public const long Manali = 210000003;

    private static readonly Dictionary<long, TripCityInfo> Cities = new()
    {
        [Nainital] = new TripCityInfo { CityKey = Nainital, Name = "Nainital", StateName = "Uttarakhand", Latitude = 29.38, Longitude = 79.45, Status = TripSpotStatuses.Ready },
        [Almora] = new TripCityInfo { CityKey = Almora, Name = "Almora", StateName = "Uttarakhand", Latitude = 29.6, Longitude = 79.66, Status = TripSpotStatuses.Ready },
        [Manali] = new TripCityInfo { CityKey = Manali, Name = "Manali", StateName = "Himachal Pradesh", Latitude = 32.24, Longitude = 77.19, Status = TripSpotStatuses.Ready },
    };

    public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(null);

    public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
        => Task.FromResult((true, Cities.GetValueOrDefault(cityKey)));

    public Task InvalidateAsync() => Task.CompletedTask;
}

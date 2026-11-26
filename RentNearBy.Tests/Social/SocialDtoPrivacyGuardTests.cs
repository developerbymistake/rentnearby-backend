using System.Reflection;
using RentNearBy.Core.DTOs.Social;

namespace RentNearBy.Tests.Social;

// Spec 6 (binding): no social answer carries a phone, email, contact detail, trip date or birth date of another person.
// Only the own profile (MyProfileDto) carries birth month and year; AdminIdentityDto is the admin-only correction answer.
public class SocialDtoPrivacyGuardTests
{
    private static readonly string[] Forbidden = ["Phone", "PhoneNumber", "Mobile", "Email", "Last4", "StartDate", "EndDate", "BirthMonth", "BirthYear"];

    private static readonly HashSet<(string Type, string Property)> Allowed =
    [
        (nameof(MyProfileDto), nameof(MyProfileDto.BirthMonth)),
        (nameof(MyProfileDto), nameof(MyProfileDto.BirthYear)),
        (nameof(AdminIdentityDto), nameof(AdminIdentityDto.BirthMonth)),
        (nameof(AdminIdentityDto), nameof(AdminIdentityDto.BirthYear)),
    ];

    private static IEnumerable<Type> Answers() => typeof(MyProfileDto).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(MyProfileDto).Namespace && t.IsClass && t.Name.EndsWith("Dto", StringComparison.Ordinal));

    [Fact]
    public void Social_answers_have_no_contact_or_date_fields()
    {
        var hits = Answers()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t.Name, Property: p.Name)))
            .Where(x => Forbidden.Any(f => x.Property.Equals(f, StringComparison.OrdinalIgnoreCase) || x.Property.EndsWith(f, StringComparison.Ordinal)))
            .Where(x => !Allowed.Contains(x))
            .Select(x => $"{x.Type}.{x.Property}")
            .ToList();
        Assert.True(hits.Count == 0, "Forbidden fields: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_guard_sees_the_social_answers() => Assert.Contains(Answers(), t => t == typeof(PublicTripDto));

    [Fact]
    public void Public_trip_carries_only_the_month_projection()
    {
        var names = typeof(PublicTripDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(PublicTripDto.When), names);
        Assert.DoesNotContain(names, n => n.Contains("Stay") || n.Contains("Taxi") || n.Contains("Expense") || n.Contains("Member"));
    }
}

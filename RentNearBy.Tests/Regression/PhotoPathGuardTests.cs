using System.Reflection;
using System.Text.RegularExpressions;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Regression;

// CONTRACT_BACKEND (card photos) R6: every stored photo path has a known cleanup. A new path column fails here until its cleanup is listed.
public class PhotoPathGuardTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Fact]
    public void Every_photo_path_column_is_known()
    {
        var found = typeof(TaxiListing).Assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("RentNearBy.Core.Entities", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Path", StringComparison.Ordinal))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "StayPhoto.FilePath", "TaxiListing.PlatePhotoPath", "TaxiListing.DisplayPhotoPath", "DistrictBanner.ImageFilePath",
                "User.ProfilePhotoPath",
            },
            found);
    }

    [Fact]
    public void Listing_photo_uploads_use_the_listing_prefix()
    {
        foreach (var (file, ids) in new[] { ("TaxiHandlers.cs", @"taxi\.Id|taxiId"), ("StayHandlers.cs", @"stay\.Id|stayId") })
        {
            var calls = Regex.Matches(Src("RentNearBy.Api", "Handlers", file), @"SavePhotoAsync\(([^;]*)\);").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, args => Assert.Matches($@",\s*userId,\s*({ids})\s*$", args));
        }
    }

    [Fact]
    public void Removal_paths_call_the_prefix_delete()
    {
        var removal = Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs");
        Assert.Equal(2, Regex.Matches(removal, @"DeleteListingPhotosBestEffortAsync\(").Count);
        Assert.Contains("DeleteListingPhotosBestEffortAsync(stay.UserId, [stay.Id])", removal);
        Assert.Contains("DeleteListingPhotosBestEffortAsync(taxi.UserId, [taxi.Id])", removal);
        Assert.Contains("DeleteAllUserPhotosBestEffortAsync(userId)", Src("RentNearBy.Infrastructure", "Services", "AccountDeletionService.cs"));
        Assert.Contains("DeleteListingPhotosBestEffortAsync(", Src("RentNearBy.Api", "Handlers", "RoleHandlers.cs"));
        Assert.Contains("DeleteListingPhotosBestEffortAsync(", Src("RentNearBy.Api", "Handlers", "AdminRoleHandlers.cs"));
    }

    // The profile picture lives inside the account-deletion prefix (bakhli/user_{id}/), outside every listing prefix.
    [Fact]
    public void Profile_photo_uses_the_user_prefix_and_account_deletion_removes_it()
    {
        var photo = Src("RentNearBy.Infrastructure", "Services", "PhotoService.cs");
        Assert.Contains("bakhli/user_{userId}/profile/", photo);
        Assert.Contains("DeleteAllUserPhotosBestEffortAsync(userId)", Src("RentNearBy.Infrastructure", "Services", "AccountDeletionService.cs"));
    }

    [Fact]
    public void Taxi_soft_delete_clears_every_photo_column()
    {
        var removal = Src("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs");
        foreach (var column in new[] { "PlatePhotoUrl", "PlatePhotoPath", "DisplayPhotoUrl", "DisplayPhotoPath" })
            Assert.Contains($"taxi.{column} = null;", removal);
    }
}

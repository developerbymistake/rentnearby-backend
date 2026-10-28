namespace RentNearBy.Core.Models;

public static class AppPlatforms
{
    public const string Android = "ANDROID";
    public const string Ios = "IOS";

    public static readonly string[] All = [Android, Ios];

    public const string AndroidPackageId = "com.rentnearby.rentnearby";
    public const string AndroidStoreDetailsPath = "/store/apps/details";
    public static readonly string AndroidStoreUrl = $"https://play.google.com{AndroidStoreDetailsPath}?id={AndroidPackageId}";

    public static readonly IReadOnlyDictionary<string, string> StoreHosts = new Dictionary<string, string>
    {
        [Android] = "play.google.com",
        [Ios] = "apps.apple.com",
    };

    public static bool TryNormalize(string? raw, out string platform)
    {
        platform = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var upper = raw.Trim().ToUpperInvariant();
        if (!All.Contains(upper)) return false;

        platform = upper;
        return true;
    }
}

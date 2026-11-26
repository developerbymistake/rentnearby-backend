namespace RentNearBy.Core.Models;

// Popular routes are decided only by the admin (owner order 2026-11-26): at most 30 rows, names are a server snapshot.
public static class PopularRouteLimits
{
    public const int Max = 30;
    public const int CityNameMax = 120;
}

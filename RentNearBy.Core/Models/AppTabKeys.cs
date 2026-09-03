namespace RentNearBy.Core.Models;

public static class AppTabKeys
{
    public const string Home = "HOME";
    public const string Rooms = "ROOMS";
    public const string Plots = "PLOTS";
    public const string Services = "SERVICES";
    public const string Profile = "PROFILE";

    // Home/Profile are structural nav slots, not togglable business verticals — AdminHandlers.UpdateAppTab
    // rejects an attempt to set IsActive=false for either, so this set is the single source of truth for
    // that rule (also used by DataSeeder to force-seed them IsEnabled = true).
    public static readonly HashSet<string> NonDeactivatable = [Home, Profile];

    public static readonly string[] All = [Home, Rooms, Plots, Services, Profile];
}

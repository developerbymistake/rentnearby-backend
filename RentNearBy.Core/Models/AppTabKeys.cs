namespace RentNearBy.Core.Models;

public static class AppTabKeys
{
    public const string Home = "HOME";
    public const string Rooms = "ROOMS";
    public const string Plots = "PLOTS";
    public const string Stays = "STAYS";
    public const string Jobs = "JOBS";
    public const string Taxi = "TAXI";
    public const string Profile = "PROFILE";

    // Home/Profile are structural nav slots, not togglable business verticals — AdminHandlers.UpdateAppTab
    // rejects an attempt to set IsActive=false for either, so this set is the single source of truth for
    // that rule (also used by DataSeeder to force-seed them IsEnabled = true).
    public static readonly HashSet<string> NonDeactivatable = [Home, Profile];

    public static readonly string[] All = [Home, Rooms, Plots, Stays, Jobs, Taxi, Profile];

    // Tabs whose consumer route group is rejected by TabGateFilter while the tab is off.
    public static readonly HashSet<string> ApiGated = [Rooms, Plots, Stays, Jobs, Taxi];

    // Inverse of UserRoles.TabKeyOf. Jobs has no role (it is a Stay Owner sub-feature).
    public static string? LinkedRoleKey(string tabKey) => tabKey switch
    {
        Rooms => UserRoles.RentOwner,
        Plots => UserRoles.PropertyOwner,
        Stays => UserRoles.StayOwner,
        Taxi => UserRoles.TaxiDriver,
        _ => null,
    };

    public static string? ForListingKind(string? listingKind) => listingKind switch
    {
        ListingKinds.Room => Rooms,
        ListingKinds.Plot => Plots,
        ListingKinds.Stay => Stays,
        ListingKinds.Taxi => Taxi,
        _ => null,
    };
}

using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Every tab decision reads one cached AppTab snapshot (AppTabCache); never query AppTab per item.
public static class TabState
{
    // Fail-open: a key with no row never takes a vertical down.
    public static bool IsActive(IReadOnlyList<AppTabDto> tabs, string? tabKey)
    {
        if (tabKey == null) return true;
        foreach (var t in tabs)
            if (t.TabKey == tabKey) return t.IsActive;
        return true;
    }

    public static bool AllActive(IReadOnlyList<AppTabDto> tabs, IEnumerable<string> tabKeys)
    {
        foreach (var key in tabKeys)
            if (!IsActive(tabs, key)) return false;
        return true;
    }

    public static bool IsRoleAvailable(IReadOnlyList<AppTabDto> tabs, Role role)
        => role.IsActive && IsActive(tabs, UserRoles.TabKeyOf(role.Id));

    public static bool IsKindActive(IReadOnlyList<AppTabDto> tabs, string? listingKind)
        => IsActive(tabs, AppTabKeys.ForListingKind(listingKind));
}

namespace RentNearBy.Core.Models.Owner;

// The owner's "Available now" (taxi) / "Accepting guests" (stay) switch across admin actions. The switch is the owner's
// choice (doc 2.2, UC-T-27); admin actions only ever turn it off.
public static class ListingSwitchRules
{
    // D-33 / FB-07: a new taxi is created offline.
    public const bool TaxiAvailableOnCreate = false;

    // Bug 2 (2026-11-24, supersedes D-33's "first approval keeps it offline"): the first approval turns the taxi on;
    // re-approval of a live edit keeps the driver's switch. A suspended owner's listing stays off.
    public static bool TaxiAvailableAfterApproval(bool everApprovedBefore, bool ownerActive, bool current)
        => ownerActive && (!everApprovedBefore || current);

    // D-13 / FB-21: suspend turns the switch off; reactivation leaves it as it is (off). The owner turns it on himself.
    public static bool AfterStatusChange(bool isActive, bool current) => isActive && current;
}

namespace RentNearBy.Core.Models.Buddy;

public static class TripAccessRules
{
    // Owner, Joined = Viewer, Left/Removed = Former; Invited, Declined, Cancelled, Expired or no row = no access (null).
    public static TripAccessLevel? Level(Guid ownerUserId, Guid callerId, string? memberStatus)
    {
        if (ownerUserId == callerId) return TripAccessLevel.Owner;
        return memberStatus switch
        {
            TripMemberStatuses.Joined => TripAccessLevel.Viewer,
            TripMemberStatuses.Left or TripMemberStatuses.Removed => TripAccessLevel.Former,
            _ => null,
        };
    }
}

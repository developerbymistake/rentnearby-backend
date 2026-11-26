namespace RentNearBy.Core.Models.Buddy;

public static class TripAccessRules
{
    // Social contract 2.5: a viewer who joined by a join request sees stay, taxi and money only from the start date.
    public static bool DetailsLocked(TripAccessLevel level, string? joinedVia, DateOnly? startDate, DateOnly todayIst)
        => level == TripAccessLevel.Viewer && joinedVia == RentNearBy.Core.Models.Social.TripJoinedVia.Request
           && (startDate is null || todayIst < startDate.Value);

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

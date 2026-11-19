namespace RentNearBy.Core.Models.TaxiRequests;

// T10 rating (O13): after a completed trip, and after a trip that was cancelled while it was running (the driver left or
// an admin cancelled it). Only a completed trip adds to RidesCompleted; both add to RatingSum and RatingCount.
public static class TaxiRatingRules
{
    public static readonly IReadOnlyList<string> MidTripEndReasons = [RequestEndReasons.DriverLeftTrip, RequestEndReasons.AdminCancelled];

    public static bool LeftDuringTrip(string requestState, string? requestEndReason, string bookingState, DateTime? bookingStartedAt)
        => requestState == RequestStates.Cancelled && bookingState == BookingStates.Cancelled && bookingStartedAt is not null
           && requestEndReason is not null && MidTripEndReasons.Contains(requestEndReason);

    public static bool CanRate(string requestState, string? requestEndReason, string bookingState, DateTime? bookingStartedAt)
        => (requestState == RequestStates.Completed && bookingState == BookingStates.Completed)
           || LeftDuringTrip(requestState, requestEndReason, bookingState, bookingStartedAt);
}

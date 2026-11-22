namespace RentNearBy.Core.Models;

// Lifetime completed counters (owner feature, D-03..D-06). The SQL in TripCompletionCounter is the same rule as one
// statement each; these pure forms are what the tests pin.
public static class TripCompletionCountRules
{
    // Seeker: the trip becomes Completed with its end date already past (IST) and at least one booking confirmed inside
    // the app. A trip without an end date never counts. Counted once.
    public static bool Counts(DateOnly? endDate, DateOnly todayIst, bool hasInAppConfirmedBooking, bool alreadyCounted)
        => endDate is { } end && end < todayIst && hasInAppConfirmedBooking && !alreadyCounted;

    // An in-app confirmed booking: an enquiry both sides confirmed, or a taxi booking the driver confirmed.
    // "I booked" (BookedByTraveller), phone bookings, shortlist items and open offers never count.
    public static bool IsInAppConfirmedEnquiry(string status) => status == TripEnquiryStatuses.ConfirmedByBoth;

    public static bool IsInAppConfirmedTaxiBooking(string status)
        => status is TaxiRequests.BookingStates.Booked or TaxiRequests.BookingStates.InProgress or TaxiRequests.BookingStates.Completed;

    // Stay Owner (and a driver's confirmed taxi lead, D-05): the enquiry is ConfirmedByBoth, its trip is Completed and its
    // check-out (last day) is already past. Counted once per enquiry.
    public static bool HostCounts(string enquiryStatus, DateOnly? checkOut, DateOnly todayIst, bool tripCompleted, bool alreadyCounted)
        => enquiryStatus == TripEnquiryStatuses.ConfirmedByBoth && checkOut is { } co && co < todayIst && tripCompleted && !alreadyCounted;

    // Taxi Driver: the booking is Completed and the ride was started in the app (D-06). Counted once per booking.
    public static bool TaxiBookingCounts(string bookingStatus, DateTime? startedAt, bool alreadyCounted)
        => bookingStatus == TaxiRequests.BookingStates.Completed && startedAt != null && !alreadyCounted;
}

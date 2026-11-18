namespace RentNearBy.Core.Models.TaxiRequests;

// Seats of a taxi request (the vehicle-type TaxiSeats in TaxiOptions.cs also allows 12+): 4, 6 or 7 (7 means 7 or more).
public static class TaxiRequestSeats
{
    public const int Four = 4;
    public const int Six = 6;
    public const int SevenPlus = 7;

    public static readonly IReadOnlyList<int> Allowed = [Four, Six, SevenPlus];

    public static bool IsValid(int seats) => Allowed.Contains(seats);

    // Travellers up to 4 give 4, up to 6 give 6, more give 7.
    public static int SuggestedFor(int travellers) => travellers <= Four ? Four : travellers <= Six ? Six : SevenPlus;

    // A driver's listing fits when its seats reach the requested seats.
    public static bool ListingFits(int listingSeats, int requestedSeats) => listingSeats >= requestedSeats;
}

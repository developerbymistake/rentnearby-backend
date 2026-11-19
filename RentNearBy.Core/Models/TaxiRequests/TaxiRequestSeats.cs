namespace RentNearBy.Core.Models.TaxiRequests;

// Seats of a taxi request: 4, 6, 7 or 12 (12 = Tempo Traveller, the vehicle-type TaxiSeats in TaxiOptions.cs). One request is
// one taxi, so a group above 12 is refused (owner decision O2).
public static class TaxiRequestSeats
{
    public const int Four = 4;
    public const int Six = 6;
    public const int Seven = 7;
    public const int Twelve = 12;
    public const int MaxTravellers = Twelve;

    public static readonly IReadOnlyList<int> Allowed = [Four, Six, Seven, Twelve];

    public static bool IsValid(int seats) => Allowed.Contains(seats);

    // The smallest allowed seats that hold the travellers; above 12 the largest (the create refuses it with TooManyTravellers).
    public static int SuggestedFor(int travellers) => Allowed.FirstOrDefault(s => s >= travellers, Twelve);

    public static bool Holds(int seats, int travellers) => seats >= travellers;

    // A driver's listing fits when its seats reach the requested seats.
    public static bool ListingFits(int listingSeats, int requestedSeats) => listingSeats >= requestedSeats;
}

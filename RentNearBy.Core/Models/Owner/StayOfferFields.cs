namespace RentNearBy.Core.Models.Owner;

// Card offer fields (old price, offer price, percent). Only a Lower rule makes them; a higher price never does.
public static class StayOfferFields
{
    // Undated card: the base FROM price with the card chip's Lower percent.
    public static (int Old, int Offer, int Percent)? Undated(int? basePerNight, int? lowerPercent)
    {
        if (basePerNight is not > 0 || lowerPercent is not > 0) return null;
        var offer = StaySpecialPriceMath.Adjust(basePerNight.Value, lower: true, lowerPercent.Value);
        return offer < basePerNight.Value ? (basePerNight.Value, offer, lowerPercent.Value) : null;
    }

    // Dated card: only when the price for the dates is below the base and the chip exists.
    public static (int Old, int Offer, int Percent)? Dated(int? basePerNight, int? datesPerNight, int? lowerPercent)
        => basePerNight is > 0 && datesPerNight is > 0 && lowerPercent is > 0 && datesPerNight < basePerNight
            ? (basePerNight.Value, datesPerNight.Value, lowerPercent.Value) : null;
}

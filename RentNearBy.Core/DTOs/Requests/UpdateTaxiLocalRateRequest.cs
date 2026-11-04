namespace RentNearBy.Core.DTOs.Requests;

// DayRate null clears both values.
public class UpdateTaxiLocalRateRequest
{
    public int? DayRate { get; set; }
    public int? KmPerDay { get; set; }
}

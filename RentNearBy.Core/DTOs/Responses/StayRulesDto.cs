namespace RentNearBy.Core.DTOs.Responses;

public class StayRulesDto
{
    public string? CheckInTime { get; set; }   // "HH:mm"
    public string? CheckOutTime { get; set; }  // "HH:mm"
    public bool PetsAllowed { get; set; }
    public bool SmokingAllowed { get; set; }
    public bool AlcoholAllowed { get; set; }
    public bool UnmarriedCouplesAllowed { get; set; }
    public bool IdProofRequired { get; set; }
    public string? CancellationPolicy { get; set; }
}

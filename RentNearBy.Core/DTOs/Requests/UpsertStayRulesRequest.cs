namespace RentNearBy.Core.DTOs.Requests;

public class UpsertStayRulesRequest
{
    public string CheckInTime { get; set; } = string.Empty;   // "HH:mm"
    public string CheckOutTime { get; set; } = string.Empty;  // "HH:mm"
    public bool PetsAllowed { get; set; }
    public bool SmokingAllowed { get; set; }
    public bool AlcoholAllowed { get; set; }
    public bool UnmarriedCouplesAllowed { get; set; }
    public bool IdProofRequired { get; set; }
    public string CancellationPolicy { get; set; } = string.Empty;
}

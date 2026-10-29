namespace RentNearBy.Core.DTOs.Responses;

public class GoLiveResultDto
{
    public DateTime? ValidUntil { get; set; }
    public string? LiveRequestStatus { get; set; }
    public LiveLimitsDto Limits { get; set; } = new();
}

public class LiveLimitsDto
{
    public int ListingLimit { get; set; }
    public int Live { get; set; }
}

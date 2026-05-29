namespace RentNearBy.Core.DTOs.Responses;

public class PlotListingPaymentVerifyResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Guid PlotMembershipId { get; set; }
    public DateTime ValidUntil { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public int MaxPlotListings { get; set; }
}

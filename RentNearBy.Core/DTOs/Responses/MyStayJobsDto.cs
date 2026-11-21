namespace RentNearBy.Core.DTOs.Responses;

public class MyStayJobsDto
{
    public List<StayJobOwnerDto> Items { get; set; } = new();
    public int ActiveCount { get; set; }
    public int MaxActive { get; set; }
    public string? StayStatus { get; set; }
    public bool CanPost { get; set; }
    // AU-22: no limit text while payment is OFF; overCap = active jobs above the plan's cap (o_st_69).
    public bool PaymentEnabled { get; set; }
    public bool CanUpgrade { get; set; }
    public bool OverCap { get; set; }
}

namespace RentNearBy.Core.DTOs.Responses;

public class MyStayJobsDto
{
    public List<StayJobOwnerDto> Items { get; set; } = new();
    public int ActiveCount { get; set; }
    public int MaxActive { get; set; }
    public string? StayStatus { get; set; }
    public bool CanPost { get; set; }
}

namespace RentNearBy.Core.DTOs.Responses;

public class AdminRoleChangeRequestDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public string UserPhone { get; set; } = string.Empty;
    public string FromRole { get; set; } = string.Empty;
    public string FromRoleName { get; set; } = string.Empty;
    public string ToRole { get; set; } = string.Empty;
    public string ToRoleName { get; set; } = string.Empty;
    public bool ToRoleAvailable { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public int RoomListingCount { get; set; }
    public int PlotListingCount { get; set; }
    public int LiveRoomListingCount { get; set; }
    public int LivePlotListingCount { get; set; }
    public int StayListingCount { get; set; }
    public int LiveStayListingCount { get; set; }   // stays under review: the only stay state that blocks a role change
    public int TaxiListingCount { get; set; }
    public int LiveTaxiListingCount { get; set; }   // taxis under review
}

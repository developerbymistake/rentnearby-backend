namespace RentNearBy.Core.DTOs.Responses;

public class AdminTaxiDto : TaxiOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public bool IsDeleted { get; set; }
}

namespace RentNearBy.Core.DTOs.Requests;

public class UpdateProfileRequest
{
    public string? Name { get; set; }
    public bool? IsContactVisible { get; set; }
    public string? PhoneNumber { get; set; }
}

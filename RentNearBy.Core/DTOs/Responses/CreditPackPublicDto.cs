namespace RentNearBy.Core.DTOs.Responses;

public class CreditPackPublicDto
{
    public Guid Id { get; set; }
    public int Credits { get; set; }
    public int BonusCredits { get; set; }
    public int TotalCredits { get; set; }
    public int PriceInr { get; set; }
    public bool IsFeatured { get; set; }
    public int SortOrder { get; set; }
}

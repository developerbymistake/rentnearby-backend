namespace RentNearBy.Core.DTOs.Responses;

public class CoinPackDto
{
    public Guid Id { get; set; }
    public int Coins { get; set; }
    public int BonusCoins { get; set; }
    public int PriceInr { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }
}

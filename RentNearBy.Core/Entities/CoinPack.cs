namespace RentNearBy.Core.Entities;

public class CoinPack
{
    public Guid Id { get; set; }
    public int Coins { get; set; }
    public int BonusCoins { get; set; } = 0;
    public int PriceInr { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; } = 999;
    public bool IsFeatured { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

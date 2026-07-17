namespace RentNearBy.Core.DTOs.Requests;

public record CreateCoinPackRequest(int Coins, int BonusCoins, int PriceInr, int SortOrder = 999, bool IsFeatured = false);

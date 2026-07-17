namespace RentNearBy.Core.DTOs.Requests;

public record UpdateCoinPackRequest(int? Coins, int? BonusCoins, int? PriceInr, int? SortOrder, bool? IsFeatured, bool? IsEnabled);

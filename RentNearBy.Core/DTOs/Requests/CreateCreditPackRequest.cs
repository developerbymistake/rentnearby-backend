namespace RentNearBy.Core.DTOs.Requests;

public record CreateCreditPackRequest(int Credits, int BonusCredits, int PriceInr, int SortOrder = 999, bool IsFeatured = false);

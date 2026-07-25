namespace RentNearBy.Core.DTOs.Requests;

public record UpdateCreditPackRequest(int? Credits, int? BonusCredits, int? PriceInr, int? SortOrder, bool? IsFeatured, bool? IsEnabled);

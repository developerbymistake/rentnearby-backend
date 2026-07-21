namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change.
public record UpdateAgentRequest(string? Name, string? Phone, string? WhatsAppNumber, bool? IsActive, int? Experience);

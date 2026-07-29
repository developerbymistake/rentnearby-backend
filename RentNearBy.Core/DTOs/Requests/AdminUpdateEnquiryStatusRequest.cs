namespace RentNearBy.Core.DTOs.Requests;

// Status must be one of RentNearBy.Core.Models.EnquiryStatuses.*. ChangedByAdminId comes from the
// authenticated ClaimsPrincipal, not this body. Note is optional context for the status-history ledger.
public record AdminUpdateEnquiryStatusRequest(string Status, string? Note);

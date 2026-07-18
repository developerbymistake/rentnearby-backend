namespace RentNearBy.Core.Entities;

// Append-only ledger, mirrors CoinTransaction's shape exactly.
public class InquiryStatusHistory
{
    public Guid Id { get; set; }
    public Guid InquiryId { get; set; }
    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.InquiryStatuses.*
    public Guid? ChangedByAdminId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public Inquiry Inquiry { get; set; } = null!;
    public Admin? ChangedByAdmin { get; set; }
}

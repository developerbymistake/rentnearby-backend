namespace RentNearBy.Core.Entities;

// Stay quotation master (0..1 per owner). No business name column: documents always print StayListing.Name.
// BankEnc is an AES-GCM blob (purpose "stay-quote-bank:<userId>"); image paths are authenticated Cloudinary ids, never URLs.
public class StayQuoteProfile
{
    public Guid UserId { get; set; }
    public string? Slogan { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public string? BankEnc { get; set; }
    public bool HasBank { get; set; }
    public string? TermsText { get; set; }
    public string? CancellationText { get; set; }
    public string? LogoPath { get; set; }
    public string? SignaturePath { get; set; }
    public int AssetsVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

namespace RentNearBy.Core.Entities;

// Append-only proof that the owner accepted a version of the quotation terms; the first time is kept.
public class StayQuoteTermsAcceptance
{
    public Guid UserId { get; set; }
    public short Version { get; set; }
    public DateTime AcceptedAt { get; set; }
    public string? AppVersion { get; set; }
}

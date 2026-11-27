namespace RentNearBy.Core.DTOs.Owner.Quote;

// PUT /stays/me/quotation/profile. No name field on purpose: the business name is always StayListing.Name (Q5);
// a "name" / "businessName" sent by a client is not bound. Plain fields are whole-object replace; null or "" clears.
public sealed class QuoteProfileRequest
{
    public string? Slogan { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public string? TermsText { get; set; }
    public string? CancellationText { get; set; }
    // Keep | Set | Clear (null reads as Keep).
    public string? BankMode { get; set; }
    public QuoteBankRequest? Bank { get; set; }
}

public sealed class QuoteBankRequest
{
    public string? Holder { get; set; }
    public string? AccountNo { get; set; }
    public string? Ifsc { get; set; }
    public string? Bank { get; set; }
    public string? Branch { get; set; }
    public string? Upi { get; set; }
}

public sealed class QuoteTermsAcceptRequest
{
    public int? Version { get; set; }
    public string? AppVersion { get; set; }
}

// POST /reserve: nothing else is accepted (no guest, no totals, no number).
public sealed class QuoteReserveRequest
{
    public Guid? DocumentId { get; set; }
    public string? Kind { get; set; }
}

public sealed class QuoteNameChangeRequest
{
    public string? Name { get; set; }
    public string? Note { get; set; }
}

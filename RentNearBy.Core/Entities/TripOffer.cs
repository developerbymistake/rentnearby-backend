using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class TripOffer
{
    public Guid Id { get; set; }
    public Guid TripEnquiryId { get; set; }
    public string Kind { get; set; } = TripOfferKinds.Offer;
    public Guid SenderUserId { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    // [TripOfferLine], TripJson.Options.
    public string LinesJson { get; set; } = "[]";
    public short Nights { get; set; }
    public int Total { get; set; }
    public string? Inclusions { get; set; }
    public string? NeedsEchoedJson { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? BalanceNote { get; set; }
    public string? CancellationText { get; set; }
    public bool? GstInvoice { get; set; }
    public string? Note { get; set; }
    // ChangeRequest only: the requested people.
    public short? Adults { get; set; }
    public short? Kids { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string Status { get; set; } = TripOfferStatuses.Pending;
    public DateTime? RespondedAt { get; set; }
    public Guid? MessageId { get; set; }
    public DateTime CreatedAt { get; set; }

    public TripEnquiry Enquiry { get; set; } = null!;
}

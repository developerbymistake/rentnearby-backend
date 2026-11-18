using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Entities.TaxiRequests;

// One ring to one driver for one request. DriverListingId and DriverUserId carry no foreign key.
public class TaxiRequestOffer
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid DriverListingId { get; set; }
    public Guid DriverUserId { get; set; }
    public string Status { get; set; } = OfferStates.Ringing;
    public string? EndReason { get; set; }
    public string? Chip { get; set; }
    public int SeqNo { get; set; }

    public DateTime OfferedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime? PushReceivedAt { get; set; }
    public bool PushLate { get; set; }
    public DateTime? SelectedAt { get; set; }
    public DateTime? ConfirmBy { get; set; }
    public DateTime? ClosedAt { get; set; }
}

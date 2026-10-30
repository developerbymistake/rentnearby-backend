using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

// Payment provider inbox (Razorpay webhooks, Play RTDN); the unique EventId makes a redelivered event a no-op.
public class PaymentEvent
{
    public Guid Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string Provider { get; set; } = PaymentProviders.Razorpay;
    public string EventType { get; set; } = string.Empty;
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string? PlayPurchaseToken { get; set; }
    public int? PlayNotificationType { get; set; }
    public string? PlayOrderId { get; set; }
    public string RawBody { get; set; } = string.Empty;
    public string? Outcome { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

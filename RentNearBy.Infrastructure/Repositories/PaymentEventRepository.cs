using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PaymentEventRepository(ApplicationDbContext context) : IPaymentEventRepository
{
    // Raw INSERT so a duplicate delivery is a no-op, not an exception that would poison the context.
    public async Task<bool> TryInsertAsync(PaymentEvent e)
    {
        var id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id;
        var receivedAt = e.ReceivedAt == default ? DateTime.UtcNow : e.ReceivedAt;
        var provider = string.IsNullOrEmpty(e.Provider) ? PaymentProviders.Razorpay : e.Provider;
        var rows = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaymentEvents" ("Id", "EventId", "Provider", "EventType", "RazorpayOrderId", "RazorpayPaymentId",
                "PlayPurchaseToken", "PlayNotificationType", "PlayOrderId", "RawBody", "Outcome", "ReceivedAt")
            VALUES ({id}, {e.EventId}, {provider}, {e.EventType}, {e.RazorpayOrderId}, {e.RazorpayPaymentId},
                {e.PlayPurchaseToken}, {e.PlayNotificationType}, {e.PlayOrderId}, {e.RawBody}, {e.Outcome}, {receivedAt})
            ON CONFLICT ("EventId") DO NOTHING
            """);
        return rows > 0;
    }

    public async Task SetOutcomeAsync(string eventId, string outcome)
        => await context.PaymentEvents
            .Where(p => p.EventId == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Outcome, outcome)
                .SetProperty(p => p.ProcessedAt, DateTime.UtcNow));

    public async Task<List<PaymentEvent>> GetByOrderIdAsync(string razorpayOrderId, int limit)
        => await context.PaymentEvents.AsNoTracking()
            .Where(p => p.RazorpayOrderId == razorpayOrderId)
            .OrderByDescending(p => p.ReceivedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<List<PaymentEvent>> GetByPlayTokenAsync(string purchaseToken, int limit)
        => await context.PaymentEvents.AsNoTracking()
            .Where(p => p.PlayPurchaseToken == purchaseToken)
            .OrderByDescending(p => p.ReceivedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<List<PaymentEvent>> GetByPlayTokenOrOrderAsync(string? purchaseToken, string? playOrderId, int limit)
    {
        if (purchaseToken == null && playOrderId == null) return [];
        return await context.PaymentEvents.AsNoTracking()
            .Where(p => (purchaseToken != null && p.PlayPurchaseToken == purchaseToken)
                || (playOrderId != null && p.PlayOrderId == playOrderId))
            .OrderByDescending(p => p.ReceivedAt)
            .Take(limit)
            .ToListAsync();
    }
}

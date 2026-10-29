using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PaymentEventRepository(ApplicationDbContext context) : IPaymentEventRepository
{
    // Raw INSERT so a duplicate delivery is a no-op, not an exception that would poison the context.
    public async Task<bool> TryInsertAsync(PaymentEvent e)
    {
        var id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id;
        var receivedAt = e.ReceivedAt == default ? DateTime.UtcNow : e.ReceivedAt;
        var rows = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaymentEvents" ("Id", "EventId", "EventType", "RazorpayOrderId", "RazorpayPaymentId", "RawBody", "Outcome", "ReceivedAt")
            VALUES ({id}, {e.EventId}, {e.EventType}, {e.RazorpayOrderId}, {e.RazorpayPaymentId}, {e.RawBody}, {e.Outcome}, {receivedAt})
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
}

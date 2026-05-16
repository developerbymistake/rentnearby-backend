using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public interface IPaymentTransactionRepository
{
    Task AddAsync(PaymentTransaction transaction);
    Task<PaymentTransaction?> GetByIdAsync(Guid id);
    Task<PaymentTransaction?> GetByRazorpayOrderIdAsync(string orderId);
    Task<IEnumerable<PaymentTransaction>> GetByUserIdAsync(Guid userId);
    Task SaveAsync();
}

public class PaymentTransactionRepository(ApplicationDbContext context) : IPaymentTransactionRepository
{
    public async Task AddAsync(PaymentTransaction transaction)
        => await context.PaymentTransactions.AddAsync(transaction);

    public async Task<PaymentTransaction?> GetByIdAsync(Guid id)
        => await context.PaymentTransactions.FirstOrDefaultAsync(t => t.Id == id);

    public async Task<PaymentTransaction?> GetByRazorpayOrderIdAsync(string orderId)
        => await context.PaymentTransactions
            .FirstOrDefaultAsync(t => t.RazorpayOrderId == orderId);

    public async Task<IEnumerable<PaymentTransaction>> GetByUserIdAsync(Guid userId)
        => await context.PaymentTransactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

    public async Task SaveAsync()
        => await context.SaveChangesAsync();
}

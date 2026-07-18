using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class CoinWalletService(IUnitOfWork unitOfWork, ApplicationDbContext context, ILogger<CoinWalletService> logger)
    : ICoinWalletService
{
    private const string SpendSavepoint = "sp_coin_spend";
    private const string CreditSavepoint = "sp_coin_credit";

    public async Task<CoinSpendResult> SpendCoinsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be greater than 0", nameof(amount));

        await unitOfWork.Wallets.EnsureExistsAsync(userId);

        var ownsTransaction = context.Database.CurrentTransaction == null;
        var transaction = ownsTransaction ? await context.Database.BeginTransactionAsync() : context.Database.CurrentTransaction!;

        try
        {
            var isOneShot = CoinTransactionReasons.OneShotDebitReasons.Contains(reason);
            if (isOneShot) await transaction.CreateSavepointAsync(SpendSavepoint);

            var newBalance = await unitOfWork.Wallets.TryDebitAsync(userId, amount);
            if (newBalance == null)
            {
                if (ownsTransaction) await transaction.RollbackAsync();
                var currentBalance = await unitOfWork.Wallets.GetBalanceAsync(userId);
                return new CoinSpendResult(CoinSpendOutcome.InsufficientBalance, currentBalance);
            }

            await unitOfWork.CoinTransactions.AddAsync(new CoinTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = -amount,
                Reason = reason,
                ReferenceId = referenceId,
                BalanceAfter = newBalance.Value,
                PerformedByUserId = performedByUserId,
                Note = note,
                CreatedAt = DateTime.UtcNow,
            });

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException) when (isOneShot)
            {
                // (UserId, Reason, ReferenceId) unique-index violation — this exact debit was already
                // processed (e.g. a retried admin-debit request). Roll back just the debit we took
                // above, not the caller's whole ambient transaction, and report it as already done.
                await transaction.RollbackToSavepointAsync(SpendSavepoint);
                var currentBalance = await unitOfWork.Wallets.GetBalanceAsync(userId);
                if (ownsTransaction) await transaction.CommitAsync();
                logger.LogInformation("SpendCoinsAsync: duplicate one-shot debit for user {UserId}, reason {Reason}, reference {ReferenceId} — already processed", userId, reason, referenceId);
                return new CoinSpendResult(CoinSpendOutcome.AlreadyProcessed, currentBalance);
            }

            if (ownsTransaction) await transaction.CommitAsync();
            return new CoinSpendResult(CoinSpendOutcome.Success, newBalance.Value);
        }
        catch
        {
            if (ownsTransaction) await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            if (ownsTransaction) await transaction.DisposeAsync();
        }
    }

    public async Task<CoinCreditResult> CreditCoinsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be greater than 0", nameof(amount));

        await unitOfWork.Wallets.EnsureExistsAsync(userId);

        var ownsTransaction = context.Database.CurrentTransaction == null;
        var transaction = ownsTransaction ? await context.Database.BeginTransactionAsync() : context.Database.CurrentTransaction!;

        try
        {
            var isOneShot = CoinTransactionReasons.OneShotCreditReasons.Contains(reason);
            if (isOneShot) await transaction.CreateSavepointAsync(CreditSavepoint);

            var newBalance = await unitOfWork.Wallets.CreditAsync(userId, amount);

            await unitOfWork.CoinTransactions.AddAsync(new CoinTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = amount,
                Reason = reason,
                ReferenceId = referenceId,
                BalanceAfter = newBalance,
                PerformedByUserId = performedByUserId,
                Note = note,
                CreatedAt = DateTime.UtcNow,
            });

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException) when (isOneShot)
            {
                // Same reasoning as SpendCoinsAsync's savepoint recovery — a retried recharge webhook,
                // coupon redemption, or admin credit for this exact (UserId, Reason, ReferenceId) must
                // land exactly once, not accumulate.
                await transaction.RollbackToSavepointAsync(CreditSavepoint);
                var currentBalance = await unitOfWork.Wallets.GetBalanceAsync(userId);
                if (ownsTransaction) await transaction.CommitAsync();
                logger.LogInformation("CreditCoinsAsync: duplicate one-shot credit for user {UserId}, reason {Reason}, reference {ReferenceId} — already credited", userId, reason, referenceId);
                return new CoinCreditResult(CoinCreditOutcome.AlreadyCredited, currentBalance);
            }

            if (ownsTransaction) await transaction.CommitAsync();
            return new CoinCreditResult(CoinCreditOutcome.Success, newBalance);
        }
        catch
        {
            if (ownsTransaction) await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            if (ownsTransaction) await transaction.DisposeAsync();
        }
    }

    public async Task<int> GetBalanceAsync(Guid userId)
        => await unitOfWork.Wallets.GetBalanceAsync(userId);
}

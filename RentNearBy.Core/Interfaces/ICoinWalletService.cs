namespace RentNearBy.Core.Interfaces;

public enum CoinSpendOutcome { Success, InsufficientBalance, AlreadyProcessed }
public enum CoinCreditOutcome { Success, AlreadyCredited }

public record CoinSpendResult(CoinSpendOutcome Outcome, int BalanceAfter);
public record CoinCreditResult(CoinCreditOutcome Outcome, int BalanceAfter);

// The one shared coin-spend/credit engine — every feature that moves coins (Go Live, coin-pack
// purchases, coupon redemption, admin manual credit/debit, and whatever future paid feature comes
// next) calls this, never its own copy of the debit/credit logic.
public interface ICoinWalletService
{
    Task<CoinSpendResult> SpendCoinsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null);
    Task<CoinCreditResult> CreditCoinsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null);
    Task<int> GetBalanceAsync(Guid userId);
}

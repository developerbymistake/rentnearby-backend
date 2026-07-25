namespace RentNearBy.Core.Interfaces;

public enum CreditSpendOutcome { Success, InsufficientBalance, AlreadyProcessed }
public enum CreditAddOutcome { Success, AlreadyCredited }

public record CreditSpendResult(CreditSpendOutcome Outcome, int BalanceAfter);
public record CreditAddResult(CreditAddOutcome Outcome, int BalanceAfter);

// The one shared spend/add engine for credits — every feature that moves credits (Go Live, credit-pack
// purchases, coupon redemption, admin manual add/debit, and whatever future paid feature comes
// next) calls this, never its own copy of the debit/add logic.
public interface ICreditWalletService
{
    Task<CreditSpendResult> SpendCreditsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null);
    Task<CreditAddResult> AddCreditsAsync(Guid userId, int amount, string reason, Guid? referenceId = null, Guid? performedByUserId = null, string? note = null);
    Task<int> GetBalanceAsync(Guid userId);
}

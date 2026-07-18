namespace RentNearBy.Core.Models;

public static class CoinTransactionReasons
{
    public const string Recharge = "RECHARGE";
    public const string CouponRedeem = "COUPON_REDEEM";
    public const string WelcomeBonus = "WELCOME_BONUS";
    public const string RoomGoLive = "ROOM_GOLIVE";
    public const string PlotGoLive = "PLOT_GOLIVE";
    public const string AdminCredit = "ADMIN_CREDIT";
    public const string AdminDebit = "ADMIN_DEBIT";

    // Credit-side reasons requiring exactly-once semantics per (UserId, Reason, ReferenceId) — a
    // retried recharge webhook, a re-submitted coupon redemption, a re-fired welcome-bonus hook, or
    // a double-tapped admin credit must each land exactly once, not accumulate.
    public static readonly string[] OneShotCreditReasons = { Recharge, CouponRedeem, WelcomeBonus, AdminCredit };

    // Debit-side reasons requiring the identical exactly-once guarantee. ROOM_GOLIVE/PLOT_GOLIVE are
    // deliberately excluded — they legitimately reuse the same listing Guid as ReferenceId across
    // renewals and must NOT be deduplicated against their own history.
    public static readonly string[] OneShotDebitReasons = { AdminDebit };

    // What the migration's partial unique index filter is generated from — the C# list and the SQL
    // filter can never drift apart because there is only one source of truth.
    public static IEnumerable<string> AllOneShotReasons => OneShotCreditReasons.Concat(OneShotDebitReasons);
}

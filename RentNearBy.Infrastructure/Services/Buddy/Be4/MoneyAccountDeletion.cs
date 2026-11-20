using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// Account deletion matrix (contract 3.3): money to settle on a trip that has not ended, or ended in the last 90 days, blocks
// the deletion. 4 statements: candidate trips (owned + member rows of any status with OpenPayments > 0), ledger (3).
// Nothing is written: his notices go by FK cascade; share and payment rows keep his id and read "Deleted user".
public sealed class MoneyAccountDeletion(ApplicationDbContext db, ITripLedger ledger, TimeProvider clock) : IAccountDeletionParticipant
{
    public const int LookbackDays = 90;

    internal const string CandidatesSql = """
        SELECT t."Id", t."Title"
        FROM "Trips" t JOIN "TripExpenseState" s ON s."TripId" = t."Id"
        WHERE t."Id" IN (SELECT o."Id" FROM "Trips" o WHERE o."OwnerUserId" = @user
                         UNION SELECT m."TripId" FROM "TripMembers" m WHERE m."UserId" = @user)
          AND s."OpenPayments" > 0
          AND (t."EndDate" IS NULL OR t."EndDate" >= @since)
        ORDER BY t."Id"
        """;

    public int Order => 10;

    public async Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
    {
        var since = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime).AddDays(-LookbackDays);
        var trips = await db.Database.SqlQueryRaw<TripRow>(CandidatesSql,
            new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
            new NpgsqlParameter("since", NpgsqlDbType.Date) { Value = since }).ToListAsync(ct);
        if (trips.Count == 0) return [];

        var snapshots = await ledger.LoadManyAsync(trips.ToDictionary(t => t.Id, _ => (IReadOnlyList<Guid>)[]), ct);
        return trips
            .Where(t => snapshots.TryGetValue(t.Id, out var s) && HasMoneyToSettle(s, userId))
            .Select(t => new AccountDeletionBlock(
                AccountDeletionReasons.SharedTripOpenBalance, string.Format(AccountDeletionReasons.SharedTripOpenBalanceMessage, t.Title)))
            .ToList();
    }

    public Task OnDeletingAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    // Pure: a non-zero settle net, or a SaidPaid row he sent or should receive.
    public static bool HasMoneyToSettle(TripLedgerSnapshot snapshot, Guid userId)
        => snapshot.People.Any(p => p.UserId == userId && p.SettleNet != 0)
           || snapshot.Payments.Any(p => p.State == PaymentStates.SaidPaid && (p.FromUserId == userId || p.ToUserId == userId));

    public sealed class TripRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
    }
}

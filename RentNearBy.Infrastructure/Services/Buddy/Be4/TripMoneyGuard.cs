using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// Trip delete guard (frame 3.11a): 1 statement when nothing is open, else + travellers (1) + ledger (3).
public sealed class TripMoneyGuard(ApplicationDbContext db, ITripAccess access, ITripLedger ledger) : ITripMoneyGuard
{
    internal const string OpenCountSql = """
        SELECT s."OpenPayments" AS "Value" FROM "TripExpenseState" s WHERE s."TripId" = @trip
        """;

    public async Task<IReadOnlyList<OpenPaymentLine>> GetOpenPaymentsAsync(Guid tripId, CancellationToken ct = default)
    {
        var open = (await db.Database.SqlQueryRaw<int>(OpenCountSql, new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId })
            .ToListAsync(ct)).FirstOrDefault();
        if (open <= 0) return [];

        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var snapshot = await ledger.LoadAsync(tripId, travellers.Select(t => t.UserId).ToList(), ct);
        var ownerId = travellers.FirstOrDefault(t => t.IsOwner)?.UserId ?? Guid.Empty;
        return Lines(snapshot, travellers, ownerId);
    }

    // SaidPaid rows and computed open rows, payer canonical order, then receiver, stored rows of a pair before its open row.
    public static IReadOnlyList<OpenPaymentLine> Lines(TripLedgerSnapshot snapshot, IReadOnlyList<TripTraveller> travellers, Guid callerId)
    {
        var order = MoneyPeople.CanonicalOrder(travellers, snapshot.People.Select(p => p.UserId)
            .Concat(snapshot.Payments.SelectMany(p => new[] { p.FromUserId, p.ToUserId })));
        var index = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var byId = new Dictionary<Guid, TripTraveller>();
        foreach (var t in travellers) byId.TryAdd(t.UserId, t);

        var stored = snapshot.Payments.Where(p => p.State == PaymentStates.SaidPaid)
            .Select(p => (p.FromUserId, p.ToUserId, p.Amount, State: PaymentStates.SaidPaid, Rank: 0));
        var computed = snapshot.Open.Select(o => (o.FromUserId, o.ToUserId, o.Amount, State: PaymentStates.Open, Rank: 1));
        return stored.Concat(computed)
            .OrderBy(r => index.GetValueOrDefault(r.FromUserId, int.MaxValue))
            .ThenBy(r => index.GetValueOrDefault(r.ToUserId, int.MaxValue))
            .ThenBy(r => r.Rank)
            .Select(r => new OpenPaymentLine(
                MoneyPeople.Person(byId.GetValueOrDefault(r.FromUserId), r.FromUserId, callerId),
                MoneyPeople.Person(byId.GetValueOrDefault(r.ToUserId), r.ToUserId, callerId),
                r.Amount, r.State))
            .ToList();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// Error null = success. Error TripNotFound is answered with the shared 404 body.
public sealed record PaymentResult(string? Error, object? ErrorData, PaymentRowDto? Payment, long BalancesVersion, bool Created = false)
{
    public static PaymentResult Fail(string error, object? data = null) => new(error, data, null, 0);
}

// E9, E10, E12, E18, E19 (contract 2.3, 5). Every write: trip row lock (also reads the title), travellers, one conditional
// write, ledger refresh, then notices and pushes as set-based statements. Fixed statement count whatever the rows.
public sealed class TripPaymentService(
    ApplicationDbContext db,
    ITripAccess access,
    ITripLedger ledger,
    NoticeWriter notices,
    NoticeReader reader,
    IBuddyLiveHints hints,
    TimeProvider clock,
    ILogger<TripPaymentService> logger)
{
    // Locks the trip only for a caller who may write here, so a stranger or a viewer on an owner route never waits on it.
    internal const string LockTripSql = """
        SELECT t."Title" AS "Value" FROM "Trips" t
        WHERE t."Id" = @trip
          AND (t."OwnerUserId" = @caller
               OR (NOT @ownerOnly AND EXISTS (SELECT 1 FROM "TripMembers" m
                                             WHERE m."TripId" = t."Id" AND m."UserId" = @caller
                                               AND m."Status" IN ('Joined', 'Left', 'Removed'))))
        FOR UPDATE OF t
        """;

    internal const string InsertSaidPaidSql = """
        INSERT INTO "TripPayments" ("Id", "TripId", "FromUserId", "ToUserId", "Amount", "State", "SaidPaidAt", "SaidPaidByUserId", "CreatedAt")
        VALUES (@id, @trip, @from, @to, @amount, 'SaidPaid', now(), @from, now())
        ON CONFLICT DO NOTHING
        """;

    internal const string ReceivedSql = """
        UPDATE "TripPayments" SET "State" = 'Received', "ReceivedAt" = now(), "ReceivedByUserId" = @caller
        WHERE "Id" = @id AND "TripId" = @trip AND "State" = 'SaidPaid'
        RETURNING "FromUserId", "ToUserId", "Amount", "State"
        """;

    internal const string WithdrawSql = """
        UPDATE "TripPayments" SET "State" = 'Withdrawn', "EndedAt" = now(), "EndedByUserId" = @caller
        WHERE "Id" = @id AND "TripId" = @trip AND "State" = 'SaidPaid' AND "FromUserId" = @caller
        RETURNING "FromUserId", "ToUserId", "Amount", "State"
        """;

    internal const string NotReceivedSql = """
        UPDATE "TripPayments" SET "State" = 'NotReceived', "EndedAt" = now(), "EndedByUserId" = @caller
        WHERE "Id" = @id AND "TripId" = @trip AND "State" = 'SaidPaid'
        RETURNING "FromUserId", "ToUserId", "Amount", "State"
        """;

    internal const string ReadPaymentSql = """
        SELECT "FromUserId", "ToUserId", "Amount", "State" FROM "TripPayments" WHERE "Id" = @id AND "TripId" = @trip
        """;

    private sealed record Unit(PaymentResult Result, long Version = 0, IReadOnlyList<Guid>? HintUsers = null);

    private sealed record Facts(string Title, IReadOnlyList<TripTraveller> Travellers, TripTraveller Me, TripTraveller Owner)
    {
        public IReadOnlyList<Guid> Ids => Travellers.Select(t => t.UserId).ToList();
    }

    public sealed class PayRow
    {
        public Guid FromUserId { get; set; }
        public Guid ToUserId { get; set; }
        public int Amount { get; set; }
        public string State { get; set; } = string.Empty;
    }

    // E9: the payer (Owner, Viewer or Former) says he paid his current open row to toUserId.
    public Task<PaymentResult> SaidPaidAsync(Guid tripId, Guid callerId, SaidPaidRequestDto req, CancellationToken ct = default)
        => RunAsync(async c =>
        {
            var (facts, denied) = await OpenAsync(tripId, callerId, ownerOnly: false, c);
            if (facts is null) return new Unit(denied!);
            var order = facts.Ids;
            var before = await ledger.LoadAsync(tripId, order, c);
            var (check, openAmount) = TripPaymentRules.CheckSaidPaid(before, req.OpId, callerId, req.ToUserId, req.Amount);
            switch (check)
            {
                case SaidPaidCheck.Replay:
                    return new Unit(new PaymentResult(null, null, StoredRow(before, facts, callerId, req.OpId, NoOne), before.Version));
                case SaidPaidCheck.BalancesChanged:
                    return new Unit(PaymentResult.Fail(BuddyErrorTypes.BalancesChanged, new { amount = openAmount }));
                case SaidPaidCheck.NothingToPay:
                    return new Unit(PaymentResult.Fail(BuddyErrorTypes.NothingToPay));
            }

            var inserted = await db.Database.ExecuteSqlRawAsync(InsertSaidPaidSql,
            [
                Uuid("id", req.OpId), Uuid("trip", tripId), Uuid("from", callerId), Uuid("to", req.ToUserId),
                new NpgsqlParameter("amount", NpgsqlDbType.Integer) { Value = req.Amount },
            ], c);
            if (inserted == 0)
                return new Unit(PaymentResult.Fail(BuddyErrorTypes.PaymentStateChanged,
                    new { payment = OpenRow(before, facts, callerId, callerId, req.ToUserId, NoOne) }));

            var after = await ledger.RefreshStateAsync(tripId, order, null, c);
            var owner = facts.Owner.UserId;
            var payerName = MoneyPeople.PushName(facts.Me, facts.Travellers);
            if (TripPaymentRules.SaidPaidNoticeToOwner(owner, callerId))
                await notices.UpsertAsync([new NoticeUpsert(owner, NoticeKinds.PaymentSaidPaid, tripId, callerId, null, req.Amount)], c);

            var pushes = new List<PushSpec>();
            if (TripPaymentRules.SaidPaidNoticeToOwner(owner, callerId))
                pushes.Add(BuddyPushCopy.PaymentSaidPaid(owner, req.OpId, tripId, payerName, req.Amount, facts.Title, facts.Owner.Name, toOwner: true));
            if (TripPaymentRules.SaidPaidCopyToReceiver(owner, callerId, req.ToUserId) && Find(facts, req.ToUserId) is not null)
                pushes.Add(BuddyPushCopy.PaymentSaidPaid(req.ToUserId, req.OpId, tripId, payerName, req.Amount, facts.Title, facts.Owner.Name, toOwner: false));
            var deletes = ReminderCleanup(after, facts);
            deletes.Add((NoticeKinds.SettleReminder, callerId));
            deletes.Add((NoticeKinds.PaymentNotReceived, callerId));
            await notices.DeletePairsAsync(tripId, deletes, c);
            await notices.PushAsync(pushes, c);

            return new Unit(new PaymentResult(null, null, StoredRow(after, facts, callerId, req.OpId, NoOne), after.Version, Created: true),
                after.Version, facts.Ids);
        }, tripId, ct);

    // E10: the owner marks a SaidPaid row Received (final).
    public Task<PaymentResult> ReceivedAsync(Guid tripId, Guid callerId, Guid paymentId, CancellationToken ct = default)
        => RunAsync(async c =>
        {
            var (facts, denied) = await OpenAsync(tripId, callerId, ownerOnly: true, c);
            if (facts is null) return new Unit(denied!);
            var row = await TransitionAsync(ReceivedSql, tripId, paymentId, callerId, c);
            if (row is null) return await NoRowAsync(tripId, paymentId, callerId, PaymentStates.Received, facts, c);

            var after = await ledger.RefreshStateAsync(tripId, facts.Ids, null, c);
            var owner = facts.Owner.UserId;
            var payerName = NameOf(facts, row.FromUserId);
            var receiverName = NameOf(facts, row.ToUserId);
            var pushes = new List<PushSpec>();
            if (TripPaymentRules.ReceivedPushToPayer(owner, row.FromUserId) && Find(facts, row.FromUserId) is not null)
                pushes.Add(BuddyPushCopy.PaymentReceived(row.FromUserId, paymentId, tripId, payerName, receiverName, row.Amount, toPayer: true));
            if (TripPaymentRules.ReceivedPushToReceiver(owner, row.FromUserId, row.ToUserId) && Find(facts, row.ToUserId) is not null)
                pushes.Add(BuddyPushCopy.PaymentReceived(row.ToUserId, paymentId, tripId, payerName, receiverName, row.Amount, toPayer: false));
            await notices.DeletePairsAsync(tripId, SaidPaidCleanup(after, facts), c);
            await notices.PushAsync(pushes, c);

            return new Unit(new PaymentResult(null, null, StoredRow(after, facts, callerId, paymentId, NoOne), after.Version), after.Version, facts.Ids);
        }, tripId, ct);

    // E18: the payer takes back his SaidPaid row ("Not paid"); the pair is Open again.
    public Task<PaymentResult> WithdrawAsync(Guid tripId, Guid callerId, Guid paymentId, CancellationToken ct = default)
        => RunAsync(async c =>
        {
            var (facts, denied) = await OpenAsync(tripId, callerId, ownerOnly: false, c);
            if (facts is null) return new Unit(denied!);
            var row = await TransitionAsync(WithdrawSql, tripId, paymentId, callerId, c);
            if (row is null) return await NoRowAsync(tripId, paymentId, callerId, PaymentStates.Withdrawn, facts, c);

            var after = await ledger.RefreshStateAsync(tripId, facts.Ids, null, c);
            var owner = facts.Owner.UserId;
            var payerName = NameOf(facts, row.FromUserId);
            var pushes = new List<PushSpec>();
            if (TripPaymentRules.WithdrawnPushToOwner(owner, row.FromUserId))
                pushes.Add(BuddyPushCopy.PaymentWithdrawn(owner, paymentId, tripId, payerName, row.Amount, facts.Title));
            if (TripPaymentRules.WithdrawnPushToReceiver(owner, row.FromUserId, row.ToUserId) && Find(facts, row.ToUserId) is not null)
                pushes.Add(BuddyPushCopy.PaymentWithdrawn(row.ToUserId, paymentId, tripId, payerName, row.Amount, facts.Title));
            await notices.DeletePairsAsync(tripId, SaidPaidCleanup(after, facts), c);
            await notices.PushAsync(pushes, c);
            var reminded = await reader.RemindedSinceAsync(tripId, DayStart(), c);

            return new Unit(new PaymentResult(null, null, OpenRow(after, facts, callerId, row.FromUserId, row.ToUserId, reminded), after.Version),
                after.Version, facts.Ids);
        }, tripId, ct);

    // E19: the owner answers "Not received"; the pair is Open again and the payer is told.
    public Task<PaymentResult> NotReceivedAsync(Guid tripId, Guid callerId, Guid paymentId, CancellationToken ct = default)
        => RunAsync(async c =>
        {
            var (facts, denied) = await OpenAsync(tripId, callerId, ownerOnly: true, c);
            if (facts is null) return new Unit(denied!);
            var row = await TransitionAsync(NotReceivedSql, tripId, paymentId, callerId, c);
            if (row is null) return await NoRowAsync(tripId, paymentId, callerId, PaymentStates.NotReceived, facts, c);

            var after = await ledger.RefreshStateAsync(tripId, facts.Ids, null, c);
            var owner = facts.Owner.UserId;
            var tellPayer = TripPaymentRules.NotReceivedToPayer(owner, row.FromUserId) && Find(facts, row.FromUserId) is not null;
            if (tellPayer)
                await notices.UpsertAsync([new NoticeUpsert(row.FromUserId, NoticeKinds.PaymentNotReceived, tripId, callerId, null, row.Amount)], c);
            await notices.DeletePairsAsync(tripId, SaidPaidCleanup(after, facts), c);
            if (tellPayer)
                await notices.PushAsync([BuddyPushCopy.PaymentNotReceived(row.FromUserId, paymentId, tripId, facts.Owner.Name, row.Amount)], c);
            var reminded = await reader.RemindedSinceAsync(tripId, DayStart(), c);

            return new Unit(new PaymentResult(null, null, OpenRow(after, facts, callerId, row.FromUserId, row.ToUserId, reminded), after.Version),
                after.Version, facts.Ids);
        }, tripId, ct);

    // E12: one reminder per payer per trip per IST day (the SettleReminder row is the limit), plus one push.
    public async Task<string?> RemindAsync(Guid tripId, Guid callerId, Guid payerId, CancellationToken ct = default)
    {
        var info = await access.GetAsync(tripId, callerId, ct);
        if (info is null) return BuddyErrorTypes.TripNotFound;
        if (info.Level != TripAccessLevel.Owner) return BuddyErrorTypes.NotTripOwner;
        if (payerId == callerId) return BuddyErrorTypes.NothingToPay;

        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        if (travellers.All(t => t.UserId != payerId)) return BuddyErrorTypes.NothingToPay;
        var snapshot = await ledger.LoadAsync(tripId, travellers.Select(t => t.UserId).ToList(), ct);
        var rows = TripPaymentRules.OpenRowsOf(snapshot, payerId);
        if (rows.Count == 0) return BuddyErrorTypes.NothingToPay;

        var now = clock.GetUtcNow().UtcDateTime;
        var total = TripPaymentRules.ClampAmount(rows.Sum(r => (long)r.Amount));
        var owed = rows.Select(r => (NameOf(travellers, r.ToUserId), (long)r.Amount)).ToList();
        var sent = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var written = await notices.UpsertIfOlderAsync(
                new NoticeUpsert(payerId, NoticeKinds.SettleReminder, tripId, callerId, null, total), TripPaymentRules.IstDayStartUtc(now), c);
            if (!written) return false;
            await notices.PushAsync([BuddyPushCopy.ManualReminder(payerId, tripId, info.Title, owed, IstTime.TodayIst(now))], c);
            await tx.CommitAsync(c);
            return true;
        }, ct, logger);
        if (!sent) return BuddyErrorTypes.ReminderAlreadySent;
        notices.Wake();
        return null;
    }

    private async Task<PaymentResult> RunAsync(Func<CancellationToken, Task<Unit>> body, Guid tripId, CancellationToken ct)
    {
        var unit = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var u = await body(c);
            if (u.HintUsers is not null) await tx.CommitAsync(c);
            return u;
        }, ct, logger);
        if (unit.HintUsers is not null)
        {
            notices.Wake();
            await hints.TripExpensesChangedAsync(tripId, unit.Version, unit.HintUsers);
        }
        return unit.Result;
    }

    // Trip lock (+ title), travellers; 404 for a stranger, 403 NotTripOwner when the owner is required, 409 OpensAtStart for a
    // request-joined member before the start date.
    private async Task<(Facts? Facts, PaymentResult? Denied)> OpenAsync(Guid tripId, Guid callerId, bool ownerOnly, CancellationToken ct)
    {
        var title = (await db.Database.SqlQueryRaw<string>(LockTripSql, Uuid("trip", tripId), Uuid("caller", callerId),
            new NpgsqlParameter("ownerOnly", NpgsqlDbType.Boolean) { Value = ownerOnly }).ToListAsync(ct)).FirstOrDefault();
        if (title is null)
            return (null, PaymentResult.Fail(ownerOnly && await access.GetAsync(tripId, callerId, ct) is not null
                ? BuddyErrorTypes.NotTripOwner : BuddyErrorTypes.TripNotFound));
        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var me = travellers.FirstOrDefault(t => t.UserId == callerId);
        var owner = travellers.FirstOrDefault(t => t.IsOwner);
        if (me is null || owner is null) return (null, PaymentResult.Fail(BuddyErrorTypes.TripNotFound));
        if (ownerOnly && !me.IsOwner) return (null, PaymentResult.Fail(BuddyErrorTypes.NotTripOwner));
        // Social contract 2.5: a member who joined by a join request has no money until the start date.
        if (!me.IsOwner && (await access.GetAsync(tripId, callerId, ct))?.DetailsLocked == true)
            return (null, PaymentResult.Fail(BuddyErrorTypes.OpensAtStart));
        return (new Facts(title, travellers, me, owner), null);
    }

    private async Task<PayRow?> TransitionAsync(string sql, Guid tripId, Guid paymentId, Guid callerId, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<PayRow>(sql, Uuid("id", paymentId), Uuid("trip", tripId), Uuid("caller", callerId)).ToListAsync(ct))
            .FirstOrDefault();

    // The conditional update touched nothing: re-read once and answer from the current state (contract 5.2).
    private async Task<Unit> NoRowAsync(Guid tripId, Guid paymentId, Guid callerId, string target, Facts facts, CancellationToken ct)
    {
        var stored = (await db.Database.SqlQueryRaw<PayRow>(ReadPaymentSql, Uuid("id", paymentId), Uuid("trip", tripId)).ToListAsync(ct))
            .FirstOrDefault();
        if (stored is null) return new Unit(PaymentResult.Fail(BuddyErrorTypes.PaymentNotFound));
        if (target == PaymentStates.Withdrawn && stored.FromUserId != callerId) return new Unit(PaymentResult.Fail(BuddyErrorTypes.NotYourDebt));

        var answer = TripPaymentRules.OnNoRow(stored.State, target);
        if (answer == NoRowAnswer.Conflict && !TripPaymentRules.ConflictShowsRow(stored.State))
            return new Unit(PaymentResult.Fail(BuddyErrorTypes.PaymentStateChanged, new { payment = (PaymentRowDto?)null }));

        var snapshot = await ledger.LoadAsync(tripId, facts.Ids, ct);
        if (answer == NoRowAnswer.Conflict)
            return new Unit(PaymentResult.Fail(BuddyErrorTypes.PaymentStateChanged, new { payment = StoredRow(snapshot, facts, callerId, paymentId, NoOne) }));
        if (target == PaymentStates.Received)
            return new Unit(new PaymentResult(null, null, StoredRow(snapshot, facts, callerId, paymentId, NoOne), snapshot.Version));
        var reminded = await reader.RemindedSinceAsync(tripId, DayStart(), ct);
        return new Unit(new PaymentResult(null, null, OpenRow(snapshot, facts, callerId, stored.FromUserId, stored.ToUserId, reminded), snapshot.Version));
    }

    private static List<(string Kind, Guid UserId)> ReminderCleanup(TripLedgerSnapshot after, Facts facts)
        => TripPaymentRules.WithoutOpenRow(after, facts.Ids).Select(id => (NoticeKinds.SettleReminder, id)).ToList();

    // The owner's PaymentSaidPaid notice goes when no SaidPaid row is left; reminders of people with nothing open go.
    private static List<(string Kind, Guid UserId)> SaidPaidCleanup(TripLedgerSnapshot after, Facts facts)
    {
        var deletes = ReminderCleanup(after, facts);
        if (after.SaidPaidCount == 0) deletes.Add((NoticeKinds.PaymentSaidPaid, facts.Owner.UserId));
        return deletes;
    }

    private static IReadOnlyList<PaymentRowDto> Rows(TripLedgerSnapshot s, Facts facts, Guid callerId, IReadOnlySet<Guid> reminded)
        => TripBalanceReader.BuildPaymentRows(
            s, MoneyPeople.CanonicalOrder(facts.Travellers, s.People.Select(p => p.UserId)), callerId, facts.Owner.UserId, reminded);

    private static PaymentRowDto? StoredRow(TripLedgerSnapshot s, Facts facts, Guid callerId, Guid paymentId, IReadOnlySet<Guid> reminded)
        => Rows(s, facts, callerId, reminded).FirstOrDefault(r => r.PaymentId == paymentId);

    private static PaymentRowDto? OpenRow(TripLedgerSnapshot s, Facts facts, Guid callerId, Guid from, Guid to, IReadOnlySet<Guid> reminded)
        => Rows(s, facts, callerId, reminded)
            .FirstOrDefault(r => r.PaymentId == null && r.State == PaymentStates.Open && r.FromUserId == from && r.ToUserId == to);

    private static TripTraveller? Find(Facts facts, Guid userId) => facts.Travellers.FirstOrDefault(t => t.UserId == userId);

    private static string NameOf(Facts facts, Guid userId) => NameOf(facts.Travellers, userId);

    private static string NameOf(IReadOnlyList<TripTraveller> travellers, Guid userId)
        => travellers.FirstOrDefault(t => t.UserId == userId) is { } t ? MoneyPeople.PushName(t, travellers) : MoneyPeople.DeletedName;

    private DateTime DayStart() => TripPaymentRules.IstDayStartUtc(clock.GetUtcNow().UtcDateTime);

    private static readonly IReadOnlySet<Guid> NoOne = new HashSet<Guid>();

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };
}

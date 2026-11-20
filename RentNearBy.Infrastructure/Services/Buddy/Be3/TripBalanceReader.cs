using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be3.Be3Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// E8 balances (6 statements: access, travellers, ledger 3, reminders + reopened title) and E17 summary (6 statements: access,
// travellers, ledger 3, one GROUPING SETS statement). Money always comes from the ledger's one grouped query.
public sealed class TripBalanceReader(ApplicationDbContext db, ITripAccess access, ITripLedger ledger, TimeProvider time)
{
    private const string ExtrasSql = """
        SELECT ARRAY(SELECT n."UserId" FROM "UserNotices" n
                     WHERE n."TripId" = @trip AND n."Kind" = 'SettleReminder' AND n."UpdatedAt" >= @dayStart) AS "RemindedIds",
               (SELECT e."Title" FROM "TripExpenses" e
                WHERE e."Id" = (SELECT st."ReopenedExpenseId" FROM "TripExpenseState" st WHERE st."TripId" = @trip)
                  AND (NOT @former OR EXISTS (SELECT 1 FROM "TripExpenseShares" m WHERE m."ExpenseId" = e."Id" AND m."UserId" = @me))) AS "ReopenedTitle"
        """;

    // GROUPING(Category, DayNumber, LinkKind, LinkId): 7 = by category, 11 = by day, 12 = by link.
    private const string GroupsSql = """
        SELECT e."Category", e."DayNumber"::int AS "Day", e."LinkKind", e."LinkId", sum(e."Amount")::bigint AS "Total",
               GROUPING(e."Category", e."DayNumber", e."LinkKind", e."LinkId")::int AS "G"
        FROM "TripExpenses" e
        WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL
        GROUP BY GROUPING SETS ((e."Category"), (e."DayNumber"), (e."LinkKind", e."LinkId"))
        """;

    // E8
    public async Task<ExpenseResult<BalancesDto>> BalancesAsync(Guid tripId, Guid callerId, CancellationToken ct = default)
    {
        var trip = await access.GetAsync(tripId, callerId, ct);
        if (trip is null) return ExpenseResult<BalancesDto>.Fail(BuddyErrorTypes.TripNotFound);
        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var order = MoneyPeople.CanonicalOrder(travellers, []);
        var snapshot = await ledger.LoadAsync(tripId, order, ct);
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var dayStart = IstTime.IstMidnightUtc(IstTime.TodayIst(nowUtc));
        var extras = (await db.Database.SqlQueryRaw<ExtrasRow>(ExtrasSql, Uuid("trip", tripId), Time("dayStart", dayStart),
            Uuid("me", callerId), Bool("former", trip.Level == TripAccessLevel.Former)).ToListAsync(ct))[0];
        return ExpenseResult<BalancesDto>.Ok(BuildBalances(trip, travellers, snapshot, callerId, extras.RemindedIds ?? [], extras.ReopenedTitle, IstTime.TodayIst(nowUtc)));
    }

    internal static BalancesDto BuildBalances(
        TripAccessInfo trip, IReadOnlyList<TripTraveller> travellers, TripLedgerSnapshot snapshot, Guid callerId,
        IReadOnlyCollection<Guid> remindedToday, string? reopenedTitle, DateOnly todayIst)
    {
        var canonical = MoneyPeople.CanonicalOrder(travellers, snapshot.People.Select(p => p.UserId));
        var former = trip.Level == TripAccessLevel.Former;
        var rows = BuildPaymentRows(snapshot, canonical, callerId, trip.OwnerUserId, remindedToday.ToHashSet()).ToList();
        if (former) rows = rows.Where(r => r.FromUserId == callerId || r.ToUserId == callerId).ToList();

        var money = snapshot.People.ToDictionary(p => p.UserId);
        var withMoney = money.Keys.ToHashSet();
        var shown = travellers.Where(t => t.IsOwner || t.Status == TripMemberStatuses.Joined || withMoney.Contains(t.UserId)).ToList();
        var people = MoneyPeople.Build(shown, withMoney, callerId)
            .Select(p =>
            {
                var m = money.GetValueOrDefault(p.UserId);
                return new BalancePersonDto { Person = p, Paid = m?.Paid ?? 0, Share = m?.Share ?? 0, Net = m?.ExpenseNet ?? 0 };
            })
            .Where(p => !former || p.Person.UserId == callerId)
            .ToList();

        var mine = money.GetValueOrDefault(callerId);
        var myRows = rows.Where(r => r.FromUserId == callerId || r.ToUserId == callerId).ToList();
        return new BalancesDto
        {
            TripId = trip.TripId,
            TripTitle = trip.Title,
            OwnerName = trip.OwnerName,
            Role = Role(trip.Level),
            IsOwner = trip.Level == TripAccessLevel.Owner,
            TripEnded = TripExpenseRules.TripEnded(trip.EndDate, todayIst),
            Frozen = TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, todayIst),
            EndDate = trip.EndDate,
            BalancesVersion = snapshot.Version,
            Settled = snapshot.Settled,
            PaymentsDone = rows.Count(r => r.State == PaymentStates.Received),
            PaymentsTotal = rows.Count,
            Reopened = snapshot.ReopenedAt is null ? null
                : new BalancesReopenedDto
                {
                    At = snapshot.ReopenedAt.Value,
                    ExpenseId = former && reopenedTitle is null ? null : snapshot.ReopenedExpenseId,
                    ExpenseTitle = reopenedTitle,
                },
            Me = new BalancesMeDto
            {
                Paid = mine?.Paid ?? 0,
                Share = mine?.Share ?? 0,
                Net = mine?.ExpenseNet ?? 0,
                Received = myRows.Where(r => r.State == PaymentStates.Received).Sum(r => (long)r.Amount),
                StillOpen = myRows.Where(r => r.State is PaymentStates.Open or PaymentStates.SaidPaid).Sum(r => (long)r.Amount),
                SaidPaidWaiting = myRows.Where(r => r.State == PaymentStates.SaidPaid).Sum(r => (long)r.Amount),
            },
            People = people,
            Payments = rows,
        };
    }

    // Stored SaidPaid/Received rows + computed open rows in the engine's WhoPaysWhom order, with the caller's buttons. Reused by BE4.
    public static IReadOnlyList<PaymentRowDto> BuildPaymentRows(
        TripLedgerSnapshot ledger, IReadOnlyList<Guid> canonicalOrder, Guid callerId, Guid ownerId,
        IReadOnlySet<Guid> remindedTodayUserIds)
    {
        var stored = ledger.Payments.Where(p => p.State is PaymentStates.SaidPaid or PaymentStates.Received).ToList();
        var byId = stored.ToDictionary(p => p.Id);
        var openByPair = ledger.Open.ToDictionary(o => (o.FromUserId, o.ToUserId));
        var canonical = TripExpenseEngine.Order(canonicalOrder,
            stored.SelectMany(p => new[] { p.FromUserId, p.ToUserId }).Concat(ledger.Open.SelectMany(o => new[] { o.FromUserId, o.ToUserId })));
        var list = TripExpenseEngine.WhoPaysWhom(
            canonical,
            stored.Select(p => new LedgerPayment(p.Id, p.FromUserId, p.ToUserId, p.Amount, PaymentStates.ToEngine(p.State))).ToList(),
            ledger.Open.Select(o => new OpenPayment(o.FromUserId, o.ToUserId, o.Amount)).ToList());
        var afterEdit = ledger.ReopenedAt is not null;
        var isOwner = callerId == ownerId;

        return list.Rows.Select(r =>
        {
            var state = PaymentStates.FromEngine(r.State);
            var p = r.PaymentId is { } id ? byId.GetValueOrDefault(id) : null;
            var open = state == PaymentStates.Open ? openByPair.GetValueOrDefault((r.FromUserId, r.ToUserId)) : null;
            return new PaymentRowDto
            {
                PaymentId = r.PaymentId,
                FromUserId = r.FromUserId,
                ToUserId = r.ToUserId,
                Amount = checked((int)r.Amount),
                State = state,
                SaidPaidAt = p?.SaidPaidAt,
                ReceivedAt = p?.ReceivedAt,
                ReceivedByUserId = p?.ReceivedByUserId,
                AfterNotReceivedAt = open?.AfterNotReceivedAt,
                AfterEdit = state == PaymentStates.Open && afterEdit,
                CanSayPaid = state == PaymentStates.Open && callerId == r.FromUserId,
                CanWithdraw = state == PaymentStates.SaidPaid && callerId == r.FromUserId,
                CanMarkReceived = state == PaymentStates.SaidPaid && isOwner,
                CanMarkNotReceived = state == PaymentStates.SaidPaid && isOwner,
                CanRemind = state == PaymentStates.Open && isOwner && r.FromUserId != ownerId,
                ReminderSentToday = state == PaymentStates.Open && remindedTodayUserIds.Contains(r.FromUserId),
            };
        }).ToList();
    }

    // E17 (Owner and Viewer; a former traveller gets 404)
    public async Task<ExpenseResult<ExpenseSummaryDto>> SummaryAsync(Guid tripId, Guid callerId, CancellationToken ct = default)
    {
        var trip = await access.GetAsync(tripId, callerId, ct);
        if (trip is null || trip.Level == TripAccessLevel.Former) return ExpenseResult<ExpenseSummaryDto>.Fail(BuddyErrorTypes.TripNotFound);
        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var order = MoneyPeople.CanonicalOrder(travellers, []);
        var snapshot = await ledger.LoadAsync(tripId, order, ct);
        var groups = await db.Database.SqlQueryRaw<GroupRow>(GroupsSql, Uuid("trip", tripId)).ToListAsync(ct);
        return ExpenseResult<ExpenseSummaryDto>.Ok(BuildSummary(trip, travellers, snapshot, groups, callerId));
    }

    internal static ExpenseSummaryDto BuildSummary(
        TripAccessInfo trip, IReadOnlyList<TripTraveller> travellers, TripLedgerSnapshot snapshot, IReadOnlyList<GroupRow> groups, Guid callerId)
    {
        var money = snapshot.People.ToDictionary(p => p.UserId);
        var withMoney = money.Keys.ToHashSet();
        var shown = travellers.Where(t => t.IsOwner || t.Status == TripMemberStatuses.Joined || withMoney.Contains(t.UserId)).ToList();
        return new ExpenseSummaryDto
        {
            TripId = trip.TripId,
            Title = trip.Title,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            DayCount = trip.DayCount,
            TravellerCount = travellers.Count(t => t.IsOwner || t.Status == TripMemberStatuses.Joined),
            Total = snapshot.TotalSpent,
            ExpenseCount = snapshot.ExpenseCount,
            Settled = snapshot.Settled,
            OpenPayments = snapshot.OpenPayments,
            PerPerson = MoneyPeople.Build(shown, withMoney, callerId).Select(p => new SummaryPersonDto
            {
                Person = p, Share = money.GetValueOrDefault(p.UserId)?.Share ?? 0, Paid = money.GetValueOrDefault(p.UserId)?.Paid ?? 0,
            }).ToList(),
            ByCategory = groups.Where(g => g.G == 7 && g.Category is not null)
                .OrderByDescending(g => g.Total).ThenBy(g => g.Category, StringComparer.Ordinal)
                .Select(g => new SummaryCategoryDto { Category = g.Category!, Amount = g.Total }).ToList(),
            ByDay = groups.Where(g => g.G == 11 && g.Day is not null).OrderBy(g => g.Day)
                .Select(g => new SummaryDayDto { Day = g.Day!.Value, Date = TripExpenseRules.DateOf(trip.StartDate, g.Day.Value), Total = g.Total }).ToList(),
            ByLink = groups.Where(g => g.G == 12 && g.LinkKind is not null && g.LinkId is not null)
                .OrderBy(g => g.LinkKind, StringComparer.Ordinal).ThenBy(g => g.LinkId)
                .Select(g => new SummaryLinkDto { Kind = g.LinkKind!, Id = g.LinkId!.Value, Total = g.Total }).ToList(),
        };
    }

    private sealed class ExtrasRow
    {
        public Guid[]? RemindedIds { get; set; }
        public string? ReopenedTitle { get; set; }
    }

    internal sealed class GroupRow
    {
        public string? Category { get; set; }
        public int? Day { get; set; }
        public string? LinkKind { get; set; }
        public Guid? LinkId { get; set; }
        public long Total { get; set; }
        public int G { get; set; }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be3.Be3Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// E1 list (owner first page 8 statements, viewer 5, next page 2) and E4 detail (4 statements). Projections only, no tracking.
public sealed class TripExpenseReader(ApplicationDbContext db, ITripAccess access, TripExpenseSuggestions suggestions, TimeProvider time)
{
    private const string HeaderSql = """
        SELECT COALESCE(st."Version", 0) AS "Version", COALESCE(st."ExpenseCount", 0) AS "ExpenseCount", COALESCE(st."TotalSpent", 0) AS "TotalSpent",
               COALESCE((SELECT sum(s."Share") FROM "TripExpenseShares" s WHERE s."TripId" = @trip AND s."UserId" = @me), 0)::bigint AS "MyShare",
               COALESCE((SELECT sum(e."Amount") FROM "TripExpenses" e WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL AND e."PaidByUserId" = @me), 0)::bigint AS "MyPaid",
               CASE WHEN @former THEN ARRAY(
                      SELECT DISTINCT e."PaidByUserId" FROM "TripExpenses" e
                      JOIN "TripExpenseShares" s ON s."ExpenseId" = e."Id" AND s."UserId" = @me
                      WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL)
                    ELSE ARRAY(
                      SELECT s."UserId" FROM "TripExpenseShares" s WHERE s."TripId" = @trip
                      UNION SELECT e."PaidByUserId" FROM "TripExpenses" e WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL) END AS "MoneyUserIds"
        FROM (SELECT 1) one
        LEFT JOIN "TripExpenseState" st ON st."TripId" = @trip
        """;

    private const string MineOnly = """
        (NOT @former OR EXISTS (SELECT 1 FROM "TripExpenseShares" m WHERE m."ExpenseId" = e."Id" AND m."UserId" = @me))
        """;

    private static readonly string DaysSql = $"""
        SELECT e."DayNumber"::int AS "Day", sum(e."Amount")::bigint AS "Total", count(*)::int AS "Count"
        FROM "TripExpenses" e
        WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL AND {MineOnly}
        GROUP BY e."DayNumber"
        ORDER BY e."DayNumber" DESC
        """;

    private static string ItemsSqlOf(string cursorFilter) => $"""
        SELECT e."Id", e."Title", e."Category", e."Amount", e."DayNumber"::int AS "DayNumber", e."PaidByUserId", e."Version",
               e."LinkKind", e."LinkId", e."CreatedAt",
               (SELECT count(*)::int FROM "TripExpenseShares" s WHERE s."ExpenseId" = e."Id") AS "SharedBy",
               COALESCE((SELECT s."Share" FROM "TripExpenseShares" s WHERE s."ExpenseId" = e."Id" AND s."UserId" = @me), 0) AS "MyShare",
               CASE e."LinkKind" WHEN 'Stay' THEN (SELECT q."Status" FROM "TripEnquiries" q WHERE q."Id" = e."LinkId")
                                 WHEN 'Taxi' THEN (SELECT b."Status" FROM "TaxiBookings" b WHERE b."Id" = e."LinkId") END AS "LinkStatus"
        FROM "TripExpenses" e
        WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL AND {MineOnly}
          {cursorFilter}
        ORDER BY e."DayNumber" DESC, e."CreatedAt" DESC, e."Id" DESC
        LIMIT @take
        """;

    private static readonly string FirstItemsSql = ItemsSqlOf(string.Empty);

    private static readonly string NextItemsSql = ItemsSqlOf("""AND (e."DayNumber", e."CreatedAt", e."Id") < (@cDay::smallint, @cAt, @cId)""");

    private const string LateSql = """
        SELECT m."UserId", COALESCE(u."Name", '') AS "Name", m."JoinedAt", c.cnt AS "Count", c.total AS "Total", c.items AS "ItemsJson"
        FROM "TripMembers" m
        JOIN "Users" u ON u."Id" = m."UserId"
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS cnt, COALESCE(sum(x."Amount"), 0)::bigint AS total,
                 (json_agg(json_build_object(
                    'expenseId', x."Id", 'title', x."Title", 'dayNumber', x."DayNumber", 'amount', x."Amount",
                    'paidByUserId', x."PaidByUserId", 'version', x."Version",
                    'shares', (SELECT json_agg(json_build_object('userId', s."UserId", 'share', s."Share")) FROM "TripExpenseShares" s WHERE s."ExpenseId" = x."Id"))
                  ORDER BY x."DayNumber", x."CreatedAt", x."Id") FILTER (WHERE x.rn <= @max))::text AS items
          FROM (
            SELECT e."Id", e."Title", e."DayNumber", e."Amount", e."PaidByUserId", e."Version", e."CreatedAt",
                   row_number() OVER (ORDER BY e."DayNumber", e."CreatedAt", e."Id") AS rn
            FROM "TripExpenses" e
            WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL AND e."SplitMode" <> 'Custom' AND e."CreatedAt" < m."JoinedAt"
              AND NOT EXISTS (SELECT 1 FROM "TripExpenseShares" s WHERE s."ExpenseId" = e."Id" AND s."UserId" = m."UserId")
          ) x
        ) c
        WHERE m."TripId" = @trip AND m."Status" = 'Joined' AND m."ExpensePromptDoneAt" IS NULL AND m."JoinedAt" IS NOT NULL AND c.cnt > 0
        ORDER BY m."JoinedAt", m."UserId"
        LIMIT 1
        """;

    private static readonly string DetailSql = $"""
        SELECT e."Id", e."Amount", e."Title", e."Category", e."DayNumber"::int AS "DayNumber", e."LinkKind", e."LinkId", e."PaidByUserId",
               e."SplitMode", e."LeftoverUserId", e."Note", e."Version", e."CreatedAt", e."UpdatedAt", e."DeletedAt",
               {SharesJsonOf} AS "SharesJson",
               {EditedTimesOf} AS "EditedTimes",
               {LinkColumns("e.\"LinkKind\"", "e.\"LinkId\"", "e.\"TripId\"", "Stored")}
        FROM "TripExpenses" e
        WHERE e."Id" = @id AND e."TripId" = @trip
        """;

    private const string HistorySql = """
        SELECT r."Version", r."CreatedAt" AS "At", r."ActorUserId", r."Kind", r."ChangesJson"::text AS "ChangesJson",
               r."SharesAfterJson"::text AS "SharesAfterJson", r."AmountAfter"
        FROM "TripExpenseRevisions" r
        WHERE r."ExpenseId" = @id
        ORDER BY r."Version" DESC
        LIMIT @max
        """;

    private DateOnly TodayIst => IstTime.TodayIst(time.GetUtcNow().UtcDateTime);

    // E1. cursor = (day, createdAt, id) of the last item of the previous page; null = first page with the header.
    public async Task<ExpenseResult<ExpenseListDto>> ListAsync(
        Guid tripId, Guid callerId, (int Day, DateTime CreatedAt, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var trip = await access.GetAsync(tripId, callerId, ct);
        if (trip is null) return ExpenseResult<ExpenseListDto>.Fail(BuddyErrorTypes.TripNotFound);
        var former = trip.Level == TripAccessLevel.Former;
        var isOwner = trip.Level == TripAccessLevel.Owner;

        if (cursor is not null)
        {
            var (pageItems, next) = await ItemsAsync(tripId, callerId, former, cursor, limit, ct);
            return ExpenseResult<ExpenseListDto>.Ok(new ExpenseListDto { Items = pageItems, NextCursor = next });
        }

        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var header = (await db.Database.SqlQueryRaw<HeaderRow>(HeaderSql, Uuid("trip", tripId), Uuid("me", callerId), Bool("former", former)).ToListAsync(ct))[0];
        var days = await db.Database.SqlQueryRaw<DayRow>(DaysSql, Uuid("trip", tripId), Uuid("me", callerId), Bool("former", former)).ToListAsync(ct);
        var (items, nextCursor) = await ItemsAsync(tripId, callerId, former, null, limit, ct);

        var today = TodayIst;
        var frozen = TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, today);
        TripExpenseSuggestions.Block? block = null;
        LatePromptDto? late = null;
        if (isOwner)
        {
            block = await suggestions.LoadAsync(tripId, trip.StartDate, trip.DayCount, frozen, ct);
            if (!frozen) late = await LatePromptAsync(tripId, ct);
        }

        var dto = new ExpenseListDto
        {
            TripId = tripId,
            TripTitle = trip.Title,
            OwnerName = trip.OwnerName,
            Role = Role(trip.Level),
            IsOwner = isOwner,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            DayCount = trip.DayCount,
            DayToday = TripExpenseRules.DayToday(trip.StartDate, trip.DayCount, today),
            TripEnded = TripExpenseRules.TripEnded(trip.EndDate, today),
            Frozen = frozen,
            ClosesOn = TripExpenseRules.ClosesOn(trip.EndDate, trip.CompletedAt),
            BalancesVersion = header.Version,
            LimitReached = !former && header.ExpenseCount >= BuddyLimits.MaxLiveExpenses,
            Totals = new ExpenseTotalsDto
            {
                Spent = former ? days.Sum(d => d.Total) : header.TotalSpent,
                ExpenseCount = former ? days.Sum(d => d.Count) : header.ExpenseCount,
                MyShare = header.MyShare,
                MyPaid = header.MyPaid,
            },
            Travellers = ListTravellers(travellers, header.MoneyUserIds ?? [], callerId, former),
            Days = days.Select(d => new ExpenseDayDto { Day = d.Day, Date = TripExpenseRules.DateOf(trip.StartDate, d.Day), Total = d.Total, Count = d.Count }).ToList(),
            Suggestions = block?.Suggestions ?? [],
            Links = block?.Links ?? [],
            LatePrompt = late,
            Items = items,
            NextCursor = nextCursor,
        };
        return ExpenseResult<ExpenseListDto>.Ok(dto);
    }

    // Owner and Viewer: current travellers + former ones that still have money. Former: himself, the owner, the payers of his expenses.
    internal static List<MoneyPersonDto> ListTravellers(IReadOnlyList<TripTraveller> travellers, IReadOnlyCollection<Guid> moneyIds, Guid callerId, bool former)
    {
        var money = moneyIds.ToHashSet();
        var shown = former
            ? travellers.Where(t => t.IsOwner || t.UserId == callerId || money.Contains(t.UserId)).ToList()
            : travellers.Where(t => t.IsOwner || t.Status == TripMemberStatuses.Joined || money.Contains(t.UserId)).ToList();
        if (former && shown.All(t => t.UserId != callerId)) money.Add(callerId);
        return MoneyPeople.Build(shown, money, callerId);
    }

    private async Task<(List<ExpenseListItemDto> Items, string? Next)> ItemsAsync(
        Guid tripId, Guid callerId, bool former, (int Day, DateTime CreatedAt, Guid Id)? cursor, int limit, CancellationToken ct)
    {
        var rows = cursor is { } key
            ? await db.Database.SqlQueryRaw<ItemRow>(NextItemsSql,
                Uuid("trip", tripId), Uuid("me", callerId), Bool("former", former),
                Int("cDay", Math.Min(key.Day, short.MaxValue)), Time("cAt", key.CreatedAt), Uuid("cId", key.Id), Int("take", limit + 1)).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<ItemRow>(FirstItemsSql,
                Uuid("trip", tripId), Uuid("me", callerId), Bool("former", former), Int("take", limit + 1)).ToListAsync(ct);
        var page = rows.Take(limit).Select(r => new ExpenseListItemDto
        {
            Id = r.Id, Title = r.Title, Category = r.Category, Amount = r.Amount, DayNumber = r.DayNumber, PaidByUserId = r.PaidByUserId,
            SharedBy = r.SharedBy, MyShare = r.MyShare, Version = r.Version, Edited = r.Version > 1, LinkKind = r.LinkKind, LinkId = r.LinkId,
            BookingCancelled = LinkCancelled(r.LinkKind, r.LinkStatus), CreatedAt = r.CreatedAt,
        }).ToList();
        var next = rows.Count > limit ? BuddyCursors.EncodeExpense(page[^1].DayNumber, page[^1].CreatedAt, page[^1].Id) : null;
        return (page, next);
    }

    private async Task<LatePromptDto?> LatePromptAsync(Guid tripId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<LateRow>(LateSql, Uuid("trip", tripId), Int("max", BuddyLimits.ShareWithMaxItems)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var items = string.IsNullOrEmpty(r.ItemsJson) ? [] : JsonSerializer.Deserialize<List<LatePromptItemDto>>(r.ItemsJson, TripExpenseRules.Json) ?? [];
        return new LatePromptDto { UserId = r.UserId, Name = r.Name, JoinedAt = r.JoinedAt, Count = r.Count, Total = r.Total, Items = items };
    }

    // E4
    public async Task<ExpenseResult<ExpenseDetailResponseDto>> DetailAsync(Guid tripId, Guid expenseId, Guid callerId, CancellationToken ct = default)
    {
        var trip = await access.GetAsync(tripId, callerId, ct);
        if (trip is null) return ExpenseResult<ExpenseDetailResponseDto>.Fail(BuddyErrorTypes.TripNotFound);
        var rows = await db.Database.SqlQueryRaw<DetailRow>(DetailSql, Uuid("trip", tripId), Uuid("id", expenseId)).ToListAsync(ct);
        if (rows.Count == 0) return ExpenseResult<ExpenseDetailResponseDto>.Fail(BuddyErrorTypes.ExpenseNotFound);
        var row = rows[0];
        var history = await db.Database.SqlQueryRaw<HistoryRow>(HistorySql, Uuid("id", expenseId), Int("max", BuddyLimits.HistoryMax)).ToListAsync(ct);
        var deleted = row.DeletedAt is not null;
        var shares = deleted
            ? TripExpenseRules.ParseShares(history.FirstOrDefault()?.SharesAfterJson)
            : TripExpenseRules.ParseShares(row.SharesJson);
        if (trip.Level == TripAccessLevel.Former && shares.All(s => s.UserId != callerId))
            return ExpenseResult<ExpenseDetailResponseDto>.Fail(BuddyErrorTypes.ExpenseNotFound);

        var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, ct);
        var lines = history.Select(h => new ExpenseHistoryLineDto
        {
            Version = h.Version, At = h.At, ActorUserId = h.ActorUserId, Kind = h.Kind, Changes = ParseChanges(h.ChangesJson),
            AmountAfter = h.AmountAfter, SharedBy = TripExpenseRules.ParseShares(h.SharesAfterJson).Count,
        }).ToList();
        var named = NamedIds(row.PaidByUserId, shares, lines);
        var canonical = MoneyPeople.CanonicalOrder(travellers, named);
        var facts = new ExpenseFacts(row.Amount, row.Title, row.Category, row.DayNumber, row.LinkKind, row.LinkId, row.PaidByUserId, row.SplitMode,
            row.LeftoverUserId, row.Note, shares);
        var canChange = trip.Level == TripAccessLevel.Owner && !deleted && !TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, TodayIst);
        return ExpenseResult<ExpenseDetailResponseDto>.Ok(new ExpenseDetailResponseDto
        {
            Expense = Detail(tripId, row.Id, facts, row.Version, row.EditedTimes, row.CreatedAt, row.UpdatedAt, deleted,
                Link(row.LinkKind, row.LinkId, row.StoredLabel, row.StoredStatus), trip.StartDate, canonical),
            People = MoneyPeople.Build(travellers.Where(t => named.Contains(t.UserId)).ToList(), named, callerId),
            History = lines,
            CanEdit = canChange,
            CanDelete = canChange,
        });
    }

    internal static List<JsonElement> ParseChanges(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }

    // Everyone named in the expense or its history: payer, sharers, actors, and the people inside paidBy/shares changes.
    internal static HashSet<Guid> NamedIds(Guid payer, IReadOnlyList<SplitShare> shares, IReadOnlyList<ExpenseHistoryLineDto> lines)
    {
        var ids = new HashSet<Guid> { payer };
        ids.UnionWith(shares.Select(s => s.UserId));
        ids.UnionWith(lines.Select(l => l.ActorUserId));
        foreach (var change in lines.SelectMany(l => l.Changes))
        {
            if (!change.TryGetProperty("field", out var field)) continue;
            var name = field.GetString();
            foreach (var key in new[] { "old", "new" })
            {
                if (!change.TryGetProperty(key, out var value)) continue;
                if (name == "paidBy" && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)) ids.Add(id);
                if (name == "shares" && value.ValueKind == JsonValueKind.Array)
                    ids.UnionWith(value.EnumerateArray()
                        .Where(s => s.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String)
                        .Select(s => Guid.TryParse(s.GetProperty("userId").GetString(), out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty));
            }
        }
        return ids;
    }

    private sealed class HeaderRow
    {
        public long Version { get; set; }
        public int ExpenseCount { get; set; }
        public long TotalSpent { get; set; }
        public long MyShare { get; set; }
        public long MyPaid { get; set; }
        public Guid[]? MoneyUserIds { get; set; }
    }

    private sealed class DayRow
    {
        public int Day { get; set; }
        public long Total { get; set; }
        public int Count { get; set; }
    }

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Amount { get; set; }
        public int DayNumber { get; set; }
        public Guid PaidByUserId { get; set; }
        public int Version { get; set; }
        public string? LinkKind { get; set; }
        public Guid? LinkId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int SharedBy { get; set; }
        public int MyShare { get; set; }
        public string? LinkStatus { get; set; }
    }

    private sealed class LateRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; }
        public int Count { get; set; }
        public long Total { get; set; }
        public string? ItemsJson { get; set; }
    }

    private sealed class DetailRow
    {
        public Guid Id { get; set; }
        public int Amount { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int DayNumber { get; set; }
        public string? LinkKind { get; set; }
        public Guid? LinkId { get; set; }
        public Guid PaidByUserId { get; set; }
        public string SplitMode { get; set; } = string.Empty;
        public Guid? LeftoverUserId { get; set; }
        public string? Note { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string? SharesJson { get; set; }
        public int EditedTimes { get; set; }
        public string? StoredLabel { get; set; }
        public string? StoredStatus { get; set; }
    }

    private sealed class HistoryRow
    {
        public int Version { get; set; }
        public DateTime At { get; set; }
        public Guid ActorUserId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string? ChangesJson { get; set; }
        public string? SharesAfterJson { get; set; }
        public int AmountAfter { get; set; }
    }
}

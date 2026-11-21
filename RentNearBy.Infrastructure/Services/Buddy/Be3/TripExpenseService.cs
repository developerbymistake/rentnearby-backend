using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Buddy.Be3.Be3Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// Expense writes E2, E3, E5, E6, E7 (contract 2.3, 5.1, 5.2). Every money write: trip lock -> access -> travellers -> probe ->
// one set-based write -> ledger refresh -> notices, all in one transaction with one retry on 40P01/40001.
public sealed class TripExpenseService(
    ApplicationDbContext db, ITripAccess access, ITripLedger ledger, INoticeWriter notices, IBuddyLiveHints hints,
    TimeProvider time, ILogger<TripExpenseService> logger)
{
    private static readonly string ProbeSql = $"""
        SELECT COALESCE(st."ExpenseCount", 0) AS "LiveCount", COALESCE(st."SaidPaidCount", 0) AS "SaidPaidCount",
               COALESCE(st."ReceivedCount", 0) AS "ReceivedCount", COALESCE(st."Version", 0) AS "StateVersion",
               e."Id" AS "ExpenseId", e."TripId" AS "ExpenseTripId", e."Amount", e."Title", e."Category", e."DayNumber"::int AS "DayNumber",
               e."LinkKind", e."LinkId", e."PaidByUserId", e."SplitMode", e."LeftoverUserId", e."Note", e."Version", e."CreatedOpId",
               e."CreatedAt", e."UpdatedAt", e."DeletedAt",
               {SharesJsonOf} AS "SharesJson",
               {EditedTimesOf} AS "EditedTimes",
               (SELECT r."Version" FROM "TripExpenseRevisions" r WHERE r."ExpenseId" = @id AND r."OpId" = @op) AS "ReplayVersion",
               {LinkColumns("e.\"LinkKind\"", "e.\"LinkId\"", "@trip", "Stored")},
               {LinkColumns("@linkKind", "@linkId", "@trip", "Wanted")}
        FROM (SELECT 1) one
        LEFT JOIN "TripExpenseState" st ON st."TripId" = @trip
        LEFT JOIN "TripExpenses" e ON e."Id" = @id
        """;

    private const string CreateSql = """
        WITH ins AS (
          INSERT INTO "TripExpenses" ("Id", "TripId", "Amount", "Title", "Category", "DayNumber", "LinkKind", "LinkId", "PaidByUserId", "SplitMode",
                                      "LeftoverUserId", "Note", "Version", "CreatedByUserId", "UpdatedByUserId", "CreatedOpId", "LastOpId", "CreatedAt", "UpdatedAt")
          VALUES (@id, @trip, @amount, @title, @category, @day, @linkKind, @linkId, @payer, @mode, @leftover, @note, 1, @actor, @actor, @op, @op, now(), now())
          ON CONFLICT ("Id") DO NOTHING
          RETURNING "Id", "Version", "CreatedAt", "UpdatedAt"
        ), sh AS (
          INSERT INTO "TripExpenseShares" ("ExpenseId", "UserId", "TripId", "Share")
          SELECT ins."Id", x.u, @trip, x.s FROM ins CROSS JOIN unnest(@users, @shares) AS x(u, s)
        ), rev AS (
          INSERT INTO "TripExpenseRevisions" ("TripId", "ExpenseId", "Version", "OpId", "ActorUserId", "Kind", "ChangesJson", "SharesAfterJson", "AmountAfter", "CreatedAt")
          SELECT @trip, ins."Id", 1, @op, @actor, 'Added', @changes::jsonb, @sharesJson::jsonb, @amount, now() FROM ins
        )
        SELECT "Id", "Version", "CreatedAt", "UpdatedAt" FROM ins
        """;

    private const string EditSql = """
        WITH up AS (
          UPDATE "TripExpenses" SET "Amount" = @amount, "Title" = @title, "Category" = @category, "DayNumber" = @day, "LinkKind" = @linkKind,
                 "LinkId" = @linkId, "PaidByUserId" = @payer, "SplitMode" = @mode, "LeftoverUserId" = @leftover, "Note" = @note,
                 "Version" = "Version" + 1, "UpdatedByUserId" = @actor, "LastOpId" = @op, "UpdatedAt" = now()
          WHERE "Id" = @id AND "TripId" = @trip AND "Version" = @base AND "DeletedAt" IS NULL
          RETURNING "Id", "Version", "CreatedAt", "UpdatedAt"
        ), sh AS (
          INSERT INTO "TripExpenseShares" ("ExpenseId", "UserId", "TripId", "Share")
          SELECT up."Id", x.u, @trip, x.s FROM up CROSS JOIN unnest(@users, @shares) AS x(u, s)
          ON CONFLICT ("ExpenseId", "UserId") DO UPDATE SET "Share" = EXCLUDED."Share"
        ), del AS (
          DELETE FROM "TripExpenseShares" s USING up WHERE s."ExpenseId" = up."Id" AND s."UserId" <> ALL(@users)
        ), rev AS (
          INSERT INTO "TripExpenseRevisions" ("TripId", "ExpenseId", "Version", "OpId", "ActorUserId", "Kind", "ChangesJson", "SharesAfterJson", "AmountAfter", "CreatedAt")
          SELECT @trip, up."Id", up."Version", @op, @actor, 'Edited', @changes::jsonb, @sharesJson::jsonb, @amount, now() FROM up
        )
        SELECT "Id", "Version", "CreatedAt", "UpdatedAt" FROM up
        """;

    private const string DeleteSql = """
        WITH up AS (
          UPDATE "TripExpenses" SET "DeletedAt" = now(), "DeletedByUserId" = @actor, "Version" = "Version" + 1,
                 "UpdatedByUserId" = @actor, "LastOpId" = @op, "UpdatedAt" = now()
          WHERE "Id" = @id AND "TripId" = @trip AND "Version" = @base AND "DeletedAt" IS NULL
          RETURNING "Id", "Version", "CreatedAt", "UpdatedAt"
        ), del AS (
          DELETE FROM "TripExpenseShares" s USING up WHERE s."ExpenseId" = up."Id"
        ), rev AS (
          INSERT INTO "TripExpenseRevisions" ("TripId", "ExpenseId", "Version", "OpId", "ActorUserId", "Kind", "ChangesJson", "SharesAfterJson", "AmountAfter", "CreatedAt")
          SELECT @trip, up."Id", up."Version", @op, @actor, 'Deleted', '[]'::jsonb, @sharesJson::jsonb, @amount, now() FROM up
        )
        SELECT "Id", "Version", "CreatedAt", "UpdatedAt" FROM up
        """;

    private static readonly string ShareWithProbeSql = $"""
        SELECT e."Id", e."Amount", e."Title", e."Category", e."DayNumber"::int AS "DayNumber", e."LinkKind", e."LinkId", e."PaidByUserId",
               e."SplitMode", e."LeftoverUserId", e."Note", e."Version", e."CreatedAt", e."UpdatedAt", e."DeletedAt",
               COALESCE(st."SaidPaidCount", 0) AS "SaidPaidCount", COALESCE(st."ReceivedCount", 0) AS "ReceivedCount",
               {SharesJsonOf} AS "SharesJson",
               {EditedTimesOf} AS "EditedTimes",
               EXISTS (SELECT 1 FROM "TripExpenseRevisions" r WHERE r."ExpenseId" = e."Id" AND r."OpId" = @op) AS "Replayed",
               {LinkColumns("e.\"LinkKind\"", "e.\"LinkId\"", "@trip", "Stored")}
        FROM "TripExpenses" e
        LEFT JOIN "TripExpenseState" st ON st."TripId" = e."TripId"
        WHERE e."Id" = ANY(@ids) AND e."TripId" = @trip
        """;

    private const string ShareWithUpdateSql = """
        WITH v AS (
          SELECT * FROM unnest(@ids, @bases, @lefts, @changes, @sharesJson, @amounts) AS v(id, base, leftover, changes, shares, amount)
        ), up AS (
          UPDATE "TripExpenses" e SET "SplitMode" = 'Chosen', "LeftoverUserId" = v.leftover, "Version" = e."Version" + 1,
                 "UpdatedByUserId" = @actor, "LastOpId" = @op, "UpdatedAt" = now()
          FROM v WHERE e."Id" = v.id AND e."TripId" = @trip AND e."Version" = v.base AND e."DeletedAt" IS NULL
          RETURNING e."Id", e."Version", e."CreatedAt", e."UpdatedAt", v.changes, v.shares, v.amount
        ), rev AS (
          INSERT INTO "TripExpenseRevisions" ("TripId", "ExpenseId", "Version", "OpId", "ActorUserId", "Kind", "ChangesJson", "SharesAfterJson", "AmountAfter", "CreatedAt")
          SELECT @trip, up."Id", up."Version", @op, @actor, 'SharedWith', up.changes::jsonb, up.shares::jsonb, up.amount, now() FROM up
        )
        SELECT "Id", "Version", "CreatedAt", "UpdatedAt" FROM up
        """;

    private const string ShareWithSharesSql = """
        WITH n AS (
          SELECT * FROM unnest(@eids, @uids, @vals) AS n(e, u, s)
        ), up AS (
          INSERT INTO "TripExpenseShares" ("ExpenseId", "UserId", "TripId", "Share")
          SELECT n.e, n.u, @trip, n.s FROM n
          ON CONFLICT ("ExpenseId", "UserId") DO UPDATE SET "Share" = EXCLUDED."Share"
        )
        DELETE FROM "TripExpenseShares" s
        WHERE s."ExpenseId" = ANY(@ids) AND NOT EXISTS (SELECT 1 FROM n WHERE n.e = s."ExpenseId" AND n.u = s."UserId")
        """;

    private const string PromptDoneSql = """
        UPDATE "TripMembers" SET "ExpensePromptDoneAt" = now()
        WHERE "TripId" = @trip AND "UserId" = @user AND "Status" = 'Joined' AND "ExpensePromptDoneAt" IS NULL
        """;

    private const string SkipSql = """
        INSERT INTO "TripExpenseSuggestionSkips" ("TripId", "SourceKind", "SourceId", "SkippedAt")
        SELECT @trip, @kind, @source, now()
        WHERE EXISTS (SELECT 1 FROM "TripEnquiries" q WHERE q."Id" = @source AND q."TripId" = @trip AND q."Kind" = 'Stay')
        ON CONFLICT ("TripId", "SourceKind", "SourceId") DO NOTHING
        """;

    private DateOnly TodayIst => IstTime.TodayIst(time.GetUtcNow().UtcDateTime);

    // E2
    public async Task<ExpenseResult<SaveExpenseResponseDto>> SaveAsync(
        Guid tripId, Guid expenseId, Guid callerId, SaveExpenseRequestDto request, CancellationToken ct = default)
    {
        var inputError = TripExpenseRules.CheckInput(request, out var input);
        if (inputError is not null) return ExpenseResult<SaveExpenseResponseDto>.Fail(inputError);

        Committed? done = null;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            done = null;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await BuddyLocks.LockOwnedTripAsync(db, tripId, callerId, c))
                return ExpenseResult<SaveExpenseResponseDto>.Fail(await access.GetAsync(tripId, callerId, c) is null ? BuddyErrorTypes.TripNotFound : BuddyErrorTypes.NotTripOwner);
            var trip = await access.GetAsync(tripId, callerId, c);
            if (trip is null) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.TripNotFound);
            if (trip.Level != TripAccessLevel.Owner) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.NotTripOwner);
            var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, c);
            var canonical = MoneyPeople.CanonicalOrder(travellers, []);
            var probe = await ProbeAsync(tripId, expenseId, request.OpId, input!.LinkKind, input.LinkId, c);
            var stored = probe.StoredFacts();
            var isCreate = request.BaseVersion == 0;
            var storedHere = probe.ExpenseId is not null && probe.ExpenseTripId == tripId;

            if (isCreate && storedHere && probe.CreatedOpId == request.OpId || !isCreate && storedHere && probe.ReplayVersion is not null)
                return ExpenseResult<SaveExpenseResponseDto>.Ok(Response(trip, probe, canonical, created: false));

            if (TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, TodayIst))
                return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.TripExpensesFrozen);
            if (isCreate)
            {
                if (probe.ExpenseId is not null) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.ExpenseIdTaken);
                if (probe.LiveCount >= BuddyLimits.MaxLiveExpenses) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.ExpenseLimitReached);
            }
            else
            {
                if (!storedHere || probe.DeletedAt is not null) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.ExpenseNotFound);
                if (probe.Version != request.BaseVersion)
                    return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.ExpenseVersionConflict,
                        new ExpenseConflictDto { Current = probe.ToDetail(tripId, trip.StartDate, canonical) });
            }
            if (input.DayNumber > TripExpenseRules.MaxDay(trip.DayCount)) return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.InvalidDay);
            var keepsStoredLink = !isCreate && stored!.LinkKind == input.LinkKind && stored.LinkId == input.LinkId;
            if (input.LinkKind is not null && probe.WantedLabel is null && !keepsStoredLink)
                return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.LinkNotInTrip);

            var current = TripExpenseRules.CurrentIds(travellers);
            var peopleError = TripExpenseRules.CheckPeople(input, current, isCreate ? null : stored);
            if (peopleError is not null) return ExpenseResult<SaveExpenseResponseDto>.Fail(peopleError);
            var split = TripExpenseRules.Split(input, canonical, current);
            if (split.Error is not null) return ExpenseResult<SaveExpenseResponseDto>.Fail(split.Error);
            var facts = TripExpenseRules.Facts(input, split);

            if (!isCreate && !TripExpenseRules.AnyChange(stored!, facts))
                return ExpenseResult<SaveExpenseResponseDto>.Ok(Response(trip, probe, canonical, created: false));
            var moneyChanges = isCreate || TripExpenseRules.MoneyChanged(stored!, facts);
            if (!isCreate && TripExpenseRules.NeedsAcknowledge(moneyChanges, probe.SaidPaidCount, probe.ReceivedCount, request.PaymentsAcknowledged))
                return ExpenseResult<SaveExpenseResponseDto>.Fail(BuddyErrorTypes.PaymentsNotAcknowledged);

            var changes = isCreate ? [] : TripExpenseRules.Diff(stored!, facts);
            var rows = await db.Database.SqlQueryRaw<WriteRow>(isCreate ? CreateSql : EditSql,
                WriteParams(tripId, expenseId, callerId, request.OpId, request.BaseVersion, facts, changes)).ToListAsync(c);
            if (rows.Count == 0)
                return ExpenseResult<SaveExpenseResponseDto>.Fail(isCreate ? BuddyErrorTypes.ExpenseIdTaken : BuddyErrorTypes.ExpenseVersionConflict);

            var row = rows[0];
            var affected = moneyChanges ? TripExpenseRules.Affected(isCreate ? null : stored, facts, trip.OwnerUserId) : [];
            var snapshot = moneyChanges ? await AfterMoneyWriteAsync(trip, travellers, canonical, expenseId, affected, [(expenseId, isCreate ? null : stored, facts)], c) : null;
            await tx.CommitAsync(c);

            var link = input.LinkKind is null ? null
                : keepsStoredLink ? Link(input.LinkKind, input.LinkId, probe.StoredLabel, probe.StoredStatus)
                : Link(input.LinkKind, input.LinkId, probe.WantedLabel, probe.WantedStatus);
            var editedTimes = probe.EditedTimes + (isCreate ? 0 : 1);
            var detail = Detail(tripId, expenseId, facts, row.Version, isCreate ? 0 : editedTimes, row.CreatedAt, row.UpdatedAt, false, link, trip.StartDate, canonical);
            var version = snapshot?.Version ?? probe.StateVersion;
            done = new Committed(tripId, version, travellers, callerId, moneyChanges);
            return ExpenseResult<SaveExpenseResponseDto>.Ok(new SaveExpenseResponseDto { Expense = detail, BalancesVersion = version, Created = isCreate }, isCreate);
        }, ct, logger);

        await AfterCommitAsync(done);
        return result;
    }

    // E3
    public async Task<ExpenseResult<BalancesVersionDto>> DeleteAsync(
        Guid tripId, Guid expenseId, Guid callerId, int baseVersion, Guid opId, bool paymentsAcknowledged, CancellationToken ct = default)
    {
        Committed? done = null;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            done = null;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await BuddyLocks.LockOwnedTripAsync(db, tripId, callerId, c))
                return ExpenseResult<BalancesVersionDto>.Fail(await access.GetAsync(tripId, callerId, c) is null ? BuddyErrorTypes.TripNotFound : BuddyErrorTypes.NotTripOwner);
            var trip = await access.GetAsync(tripId, callerId, c);
            if (trip is null) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.TripNotFound);
            if (trip.Level != TripAccessLevel.Owner) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.NotTripOwner);
            var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, c);
            var canonical = MoneyPeople.CanonicalOrder(travellers, []);
            var probe = await ProbeAsync(tripId, expenseId, opId, null, null, c);
            var storedHere = probe.ExpenseId is not null && probe.ExpenseTripId == tripId;
            if (!storedHere) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.ExpenseNotFound);
            if (probe.ReplayVersion is not null) return ExpenseResult<BalancesVersionDto>.Ok(new BalancesVersionDto { BalancesVersion = probe.StateVersion });
            if (TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, TodayIst)) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.TripExpensesFrozen);
            if (probe.DeletedAt is not null) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.ExpenseNotFound);
            if (probe.Version != baseVersion)
                return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.ExpenseVersionConflict,
                    new ExpenseConflictDto { Current = probe.ToDetail(tripId, trip.StartDate, canonical) });
            if (TripExpenseRules.NeedsAcknowledge(true, probe.SaidPaidCount, probe.ReceivedCount, paymentsAcknowledged))
                return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.PaymentsNotAcknowledged);

            var stored = probe.StoredFacts()!;
            var rows = await db.Database.SqlQueryRaw<WriteRow>(DeleteSql,
                Uuid("id", expenseId), Uuid("trip", tripId), Uuid("actor", callerId), Uuid("op", opId), Int("base", baseVersion),
                Text("sharesJson", TripExpenseRules.SharesJson(stored.Shares)), Int("amount", stored.Amount)).ToListAsync(c);
            if (rows.Count == 0) return ExpenseResult<BalancesVersionDto>.Fail(BuddyErrorTypes.ExpenseVersionConflict);

            var affected = TripExpenseRules.Affected(stored, null, trip.OwnerUserId);
            var snapshot = await AfterMoneyWriteAsync(trip, travellers, canonical, expenseId, affected, [(expenseId, stored, null)], c);
            await tx.CommitAsync(c);
            done = new Committed(tripId, snapshot.Version, travellers, callerId, true);
            return ExpenseResult<BalancesVersionDto>.Ok(new BalancesVersionDto { BalancesVersion = snapshot.Version });
        }, ct, logger);

        await AfterCommitAsync(done);
        return result;
    }

    // E7
    public async Task<ExpenseResult<ShareWithResponseDto>> ShareWithAsync(
        Guid tripId, Guid callerId, ShareWithRequestDto request, CancellationToken ct = default)
    {
        var items = (request.Items ?? []).GroupBy(i => i.ExpenseId).Select(g => g.First()).ToList();
        Committed? done = null;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            done = null;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await BuddyLocks.LockOwnedTripAsync(db, tripId, callerId, c))
                return ExpenseResult<ShareWithResponseDto>.Fail(await access.GetAsync(tripId, callerId, c) is null ? BuddyErrorTypes.TripNotFound : BuddyErrorTypes.NotTripOwner);
            var trip = await access.GetAsync(tripId, callerId, c);
            if (trip is null) return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.TripNotFound);
            if (trip.Level != TripAccessLevel.Owner) return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.NotTripOwner);
            var travellers = await access.GetTravellersAsync(tripId, includeFormer: true, c);
            var canonical = MoneyPeople.CanonicalOrder(travellers, []);
            var rows = await db.Database.SqlQueryRaw<ShareWithRow>(ShareWithProbeSql,
                Uuid("trip", tripId), Uuid("op", request.OpId), Uuids("ids", items.Select(i => i.ExpenseId))).ToListAsync(c);
            var byId = rows.ToDictionary(r => r.Id);

            if (rows.Count > 0 && rows.Any(r => r.Replayed))
                return ExpenseResult<ShareWithResponseDto>.Ok(new ShareWithResponseDto
                {
                    Expenses = items.Where(i => byId.ContainsKey(i.ExpenseId)).Select(i => byId[i.ExpenseId].ToDetail(tripId, trip.StartDate, canonical)).ToList(),
                    BalancesVersion = await StateVersionAsync(tripId, c),
                });
            if (TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, TodayIst)) return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.TripExpensesFrozen);
            if (!travellers.Any(t => t.UserId == request.UserId && !t.IsOwner && t.Status == TripMemberStatuses.Joined))
                return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.NotTraveller);
            if (items.Any(i => !byId.TryGetValue(i.ExpenseId, out var r) || r.DeletedAt is not null))
                return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.ExpenseNotFound);
            var conflicts = items.Where(i => byId[i.ExpenseId].Version != i.BaseVersion).Select(i => i.ExpenseId).ToList();
            if (conflicts.Count > 0)
                return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.ExpenseVersionConflict, new ExpenseConflictsDto { Conflicts = conflicts });
            foreach (var item in items)
            {
                var r = byId[item.ExpenseId];
                var error = TripExpenseRules.CheckShareWith(r.SplitMode, TripExpenseRules.ParseShares(r.SharesJson), request.UserId);
                if (error is not null) return ExpenseResult<ShareWithResponseDto>.Fail(error);
            }
            if (TripExpenseRules.NeedsAcknowledge(true, rows[0].SaidPaidCount, rows[0].ReceivedCount, request.PaymentsAcknowledged))
                return ExpenseResult<ShareWithResponseDto>.Fail(BuddyErrorTypes.PaymentsNotAcknowledged);

            var plans = items.Select(i =>
            {
                var r = byId[i.ExpenseId];
                var old = r.Facts();
                var split = TripExpenseEngine.ShareWith(old.Amount, canonical, old.Shares, old.PaidByUserId, request.UserId);
                var now = old with { SplitMode = SplitModes.Chosen, LeftoverUserId = split.LeftoverUserId, Shares = split.Shares };
                return (Row: r, Base: i.BaseVersion, Old: old, Now: now);
            }).ToList();

            await db.Database.ExecuteSqlRawAsync(PromptDoneSql, [Uuid("trip", tripId), Uuid("user", request.UserId)], c);
            var written = await db.Database.SqlQueryRaw<WriteRow>(ShareWithUpdateSql,
                Uuid("trip", tripId), Uuid("actor", callerId), Uuid("op", request.OpId),
                Uuids("ids", plans.Select(p => p.Row.Id)), Ints("bases", plans.Select(p => p.Base)),
                NullableUuids("lefts", plans.Select(p => p.Now.LeftoverUserId)),
                Texts("changes", plans.Select(p => TripExpenseRules.ChangesJson(TripExpenseRules.Diff(p.Old, p.Now)))),
                Texts("sharesJson", plans.Select(p => TripExpenseRules.SharesJson(p.Now.Shares))),
                Ints("amounts", plans.Select(p => p.Old.Amount))).ToListAsync(c);
            if (written.Count != plans.Count) throw new InvalidOperationException("Share-with wrote a different number of expenses than it checked under the trip lock.");

            var pairs = plans.SelectMany(p => p.Now.Shares.Select(s => (p.Row.Id, s.UserId, s.Share))).ToList();
            await db.Database.ExecuteSqlRawAsync(ShareWithSharesSql,
            [
                Uuid("trip", tripId), Uuids("ids", plans.Select(p => p.Row.Id)),
                Uuids("eids", pairs.Select(p => p.Item1)), Uuids("uids", pairs.Select(p => p.UserId)), Ints("vals", pairs.Select(p => p.Share)),
            ], c);

            var affected = plans.SelectMany(p => TripExpenseRules.Affected(p.Old, p.Now, trip.OwnerUserId)).Distinct().ToList();
            var snapshot = await AfterMoneyWriteAsync(trip, travellers, canonical, plans[0].Row.Id, affected,
                plans.Select(p => (p.Row.Id, (ExpenseFacts?)p.Old, (ExpenseFacts?)p.Now)).ToList(), c);
            await tx.CommitAsync(c);

            var writtenById = written.ToDictionary(w => w.Id);
            var expenses = plans.Select(p =>
            {
                var w = writtenById[p.Row.Id];
                return Detail(tripId, p.Row.Id, p.Now, w.Version, p.Row.EditedTimes + 1, w.CreatedAt, w.UpdatedAt, false,
                    Link(p.Now.LinkKind, p.Now.LinkId, p.Row.StoredLabel, p.Row.StoredStatus), trip.StartDate, canonical);
            }).ToList();
            done = new Committed(tripId, snapshot.Version, travellers, callerId, true);
            return ExpenseResult<ShareWithResponseDto>.Ok(new ShareWithResponseDto
            {
                Expenses = expenses,
                BalancesVersion = snapshot.Version,
                Notified = Recipients(affected, travellers, trip.OwnerUserId),
            });
        }, ct, logger);

        await AfterCommitAsync(done);
        return result;
    }

    // E5
    public async Task<ExpenseResult<bool>> SkipSuggestionAsync(Guid tripId, Guid callerId, Guid sourceId, CancellationToken ct = default)
    {
        var gate = await OwnerOpenAsync(tripId, callerId, ct);
        if (gate is not null) return ExpenseResult<bool>.Fail(gate);
        await db.Database.ExecuteSqlRawAsync(SkipSql, [Uuid("trip", tripId), Text("kind", ExpenseLinkKinds.Stay), Uuid("source", sourceId)], ct);
        return ExpenseResult<bool>.Ok(true);
    }

    // E6
    public async Task<ExpenseResult<bool>> KeepAsync(Guid tripId, Guid callerId, Guid userId, CancellationToken ct = default)
    {
        var gate = await OwnerOpenAsync(tripId, callerId, ct);
        if (gate is not null) return ExpenseResult<bool>.Fail(gate);
        await db.Database.ExecuteSqlRawAsync(PromptDoneSql, [Uuid("trip", tripId), Uuid("user", userId)], ct);
        return ExpenseResult<bool>.Ok(true);
    }

    private async Task<string?> OwnerOpenAsync(Guid tripId, Guid callerId, CancellationToken ct)
    {
        var trip = await access.GetAsync(tripId, callerId, ct);
        if (trip is null) return BuddyErrorTypes.TripNotFound;
        if (trip.Level != TripAccessLevel.Owner) return BuddyErrorTypes.NotTripOwner;
        return TripExpenseRules.Frozen(trip.EndDate, trip.CompletedAt, TodayIst) ? BuddyErrorTypes.TripExpensesFrozen : null;
    }

    // Ledger refresh, one notice row (ExpenseAdded, ShareChanged or ExpensesChanged) + coalesced push for the people whose money
    // changed, SettleReminder clean-up.
    private async Task<TripLedgerSnapshot> AfterMoneyWriteAsync(
        TripAccessInfo trip, IReadOnlyList<TripTraveller> travellers, IReadOnlyList<Guid> canonical, Guid changedExpenseId,
        IReadOnlyCollection<Guid> affected, IReadOnlyList<(Guid ExpenseId, ExpenseFacts? Old, ExpenseFacts? Now)> changes, CancellationToken ct)
    {
        var snapshot = await ledger.RefreshStateAsync(trip.TripId, canonical, changedExpenseId, ct);
        var recipients = Recipients(affected, travellers, trip.OwnerUserId);
        if (recipients.Count > 0)
        {
            var owner = travellers.FirstOrDefault(t => t.IsOwner);
            var ownerName = owner is null ? trip.OwnerName : MoneyPeople.PushName(owner, travellers);
            var nowUtc = time.GetUtcNow().UtcDateTime;
            await notices.UpsertAsync(TripExpenseRules.MoneyNotices(trip.TripId, trip.OwnerUserId, recipients, changes), ct);
            await notices.PushAsync(recipients.Select(u => BuddyPushCopy.TripExpensesUpdated(u, trip.TripId, ownerName, trip.Title, nowUtc)).ToList(), ct);
        }
        var payers = snapshot.Open.Select(o => o.FromUserId).ToHashSet();
        var clear = travellers.Select(t => t.UserId).Where(id => !payers.Contains(id)).Distinct().ToList();
        if (clear.Count > 0) await notices.DeleteAsync(NoticeKinds.SettleReminder, trip.TripId, clear, ct);
        return snapshot;
    }

    // Known accounts only (a deleted account has no Users row, so no notice), never the owner.
    private static List<Guid> Recipients(IEnumerable<Guid> affected, IReadOnlyList<TripTraveller> travellers, Guid ownerId)
    {
        var known = travellers.Select(t => t.UserId).ToHashSet();
        return affected.Where(id => id != ownerId && known.Contains(id)).Distinct().ToList();
    }

    private async Task AfterCommitAsync(Committed? done)
    {
        if (done is null || !done.MoneyChanged) return;
        notices.Wake();
        var others = done.Travellers.Select(t => t.UserId).Where(id => id != done.CallerId).Distinct().ToList();
        if (others.Count == 0) return;
        try
        {
            await hints.TripExpensesChangedAsync(done.TripId, done.Version, others);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "buddy.expenses.hint_failed {TripId}", done.TripId);
        }
    }

    private async Task<ProbeRow> ProbeAsync(Guid tripId, Guid expenseId, Guid opId, string? linkKind, Guid? linkId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ProbeRow>(ProbeSql,
            Uuid("trip", tripId), Uuid("id", expenseId), Uuid("op", opId), Text("linkKind", linkKind), Uuid("linkId", linkId)).ToListAsync(ct);
        return rows[0];
    }

    private async Task<long> StateVersionAsync(Guid tripId, CancellationToken ct)
        => await db.Set<Core.Entities.Buddy.TripExpenseState>().AsNoTracking()
            .Where(s => s.TripId == tripId).Select(s => s.Version).FirstOrDefaultAsync(ct);

    private static SaveExpenseResponseDto Response(TripAccessInfo trip, ProbeRow probe, IReadOnlyList<Guid> canonical, bool created) => new()
    {
        Expense = probe.ToDetail(trip.TripId, trip.StartDate, canonical),
        BalancesVersion = probe.StateVersion,
        Created = created,
    };

    private static NpgsqlParameter[] WriteParams(
        Guid tripId, Guid expenseId, Guid actor, Guid opId, int baseVersion, ExpenseFacts f, IReadOnlyList<ExpenseChange> changes) =>
    [
        Uuid("id", expenseId), Uuid("trip", tripId), Int("amount", f.Amount), Text("title", f.Title), Text("category", f.Category),
        Small("day", f.DayNumber), Text("linkKind", f.LinkKind), Uuid("linkId", f.LinkId), Uuid("payer", f.PaidByUserId),
        Text("mode", f.SplitMode), Uuid("leftover", f.LeftoverUserId), Text("note", f.Note), Uuid("actor", actor), Uuid("op", opId),
        Int("base", baseVersion), Uuids("users", f.Shares.Select(s => s.UserId)), Ints("shares", f.Shares.Select(s => s.Share)),
        Text("changes", TripExpenseRules.ChangesJson(changes)), Text("sharesJson", TripExpenseRules.SharesJson(f.Shares)),
    ];

    private sealed record Committed(Guid TripId, long Version, IReadOnlyList<TripTraveller> Travellers, Guid CallerId, bool MoneyChanged);

    private sealed class WriteRow
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class ProbeRow
    {
        public int LiveCount { get; set; }
        public int SaidPaidCount { get; set; }
        public int ReceivedCount { get; set; }
        public long StateVersion { get; set; }
        public Guid? ExpenseId { get; set; }
        public Guid? ExpenseTripId { get; set; }
        public int? Amount { get; set; }
        public string? Title { get; set; }
        public string? Category { get; set; }
        public int? DayNumber { get; set; }
        public string? LinkKind { get; set; }
        public Guid? LinkId { get; set; }
        public Guid? PaidByUserId { get; set; }
        public string? SplitMode { get; set; }
        public Guid? LeftoverUserId { get; set; }
        public string? Note { get; set; }
        public int? Version { get; set; }
        public Guid? CreatedOpId { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string? SharesJson { get; set; }
        public int EditedTimes { get; set; }
        public int? ReplayVersion { get; set; }
        public string? StoredLabel { get; set; }
        public string? StoredStatus { get; set; }
        public string? WantedLabel { get; set; }
        public string? WantedStatus { get; set; }

        public ExpenseFacts? StoredFacts() => ExpenseId is null ? null : new ExpenseFacts(
            Amount ?? 0, Title ?? string.Empty, Category ?? string.Empty, DayNumber ?? 1, LinkKind, LinkId,
            PaidByUserId ?? Guid.Empty, SplitMode ?? string.Empty, LeftoverUserId, Note, TripExpenseRules.ParseShares(SharesJson));

        public ExpenseDetailDto ToDetail(Guid tripId, DateOnly? startDate, IReadOnlyList<Guid> canonical)
            => Detail(tripId, ExpenseId!.Value, StoredFacts()!, Version ?? 0, EditedTimes, CreatedAt ?? default, UpdatedAt ?? default,
                DeletedAt is not null, Link(LinkKind, LinkId, StoredLabel, StoredStatus), startDate, canonical);
    }

    private sealed class ShareWithRow
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
        public int SaidPaidCount { get; set; }
        public int ReceivedCount { get; set; }
        public string? SharesJson { get; set; }
        public int EditedTimes { get; set; }
        public bool Replayed { get; set; }
        public string? StoredLabel { get; set; }
        public string? StoredStatus { get; set; }

        public ExpenseFacts Facts() => new(Amount, Title, Category, DayNumber, LinkKind, LinkId, PaidByUserId, SplitMode, LeftoverUserId, Note,
            TripExpenseRules.ParseShares(SharesJson));

        public ExpenseDetailDto ToDetail(Guid tripId, DateOnly? startDate, IReadOnlyList<Guid> canonical)
            => Detail(tripId, Id, Facts(), Version, EditedTimes, CreatedAt, UpdatedAt, DeletedAt is not null,
                Link(LinkKind, LinkId, StoredLabel, StoredStatus), startDate, canonical);
    }
}

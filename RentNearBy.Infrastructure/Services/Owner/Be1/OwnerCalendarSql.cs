using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// Lane BE1 statements (contract 2.1, 2.2). Listing scope is always @k (listing kind) and @l (listing id) on the calendar
// indexes ix_calendarentries_listing_dateto / _datefrom / _manual_active; blocks use ix_stayblockedranges_stay_dateto.
public static class OwnerCalendarSql
{
    public const string Manual = CalendarEntryKinds.Manual;
    public const string Auto = CalendarEntryKinds.Auto;

    // Every column of CalendarEntryRow over alias c; @available decides the SourceAvailable expression, inactive the
    // InactiveByPlan one (needs @today and @limit).
    public static string Columns(string alias = "c", bool sourceAvailable = false, bool inactive = false) => $"""
        {alias}."Id", {alias}."OwnerUserId", {alias}."ListingKind", {alias}."ListingId", {alias}."Kind", {alias}."SourceType", {alias}."SourceId",
        {alias}."TravellerUserId", {alias}."DateFrom", {alias}."DateTo", {alias}."TimeOfDay", {alias}."FromPlace", {alias}."ToPlace", {alias}."Label",
        {alias}."Detail", {alias}."Note", {alias}."ListedTotal", {alias}."AgreedTotal", {alias}."Guests", {alias}."Seats", {alias}."RoomNames",
        {alias}."PlacesJson"::text AS "PlacesJson", {alias}."FinalAmount", {alias}."AdvanceReceived", {alias}."PaidInFull", {alias}."Cancelled",
        {alias}."CancelledBy", {alias}."CancelledOn", {alias}."Version", {alias}."CreatedAt",
        {(sourceAvailable ? SourceAvailable(alias) : "NULL::boolean")} AS "SourceAvailable",
        {(inactive ? $"NOT {Visible(alias)}" : "false")} AS "InactiveByPlan"
        """;

    private static string SourceAvailable(string a) => $"""
        CASE {a}."SourceType"
          WHEN '{CalendarSourceTypes.TaxiBooking}' THEN EXISTS (SELECT 1 FROM "TaxiBookings" sb WHERE sb."Id" = {a}."SourceId")
          WHEN '{CalendarSourceTypes.StayEnquiry}' THEN EXISTS (SELECT 1 FROM "TripEnquiries" se WHERE se."Id" = {a}."SourceId")
          WHEN '{CalendarSourceTypes.TaxiLead}' THEN EXISTS (SELECT 1 FROM "TripEnquiries" se WHERE se."Id" = {a}."SourceId")
          ELSE NULL END
        """;

    // The `limit` active manual rows added last, (CreatedAt DESC, Id) (doc 6.1.10, plan rule: the last added stay).
    public const string VisibleManualSet = """
        SELECT m."Id", m."DateFrom" FROM "CalendarEntries" m
        WHERE m."ListingKind" = @k AND m."ListingId" = @l AND m."Kind" = 'Manual' AND m."DateTo" >= @today
        ORDER BY m."CreatedAt" DESC, m."Id" LIMIT @limit
        """;

    // One predicate for C1, C2, C3: Auto always, past rows always, the visible manual set.
    public static string Visible(string alias = "c")
        => $"""({alias}."Kind" = 'Auto' OR {alias}."DateTo" < @today OR {alias}."Id" IN (SELECT v."Id" FROM ({VisibleManualSet}) v))""";

    public const string ActiveManualCount = """
        SELECT count(*) FROM "CalendarEntries" m
        WHERE m."ListingKind" = @k AND m."ListingId" = @l AND m."Kind" = 'Manual' AND m."DateTo" >= @today
        """;

    // Owner context extras (OwnerContextReader X1..X4; listing alias l, @today): active manual count, active blocks, active special prices.
    public static IReadOnlyList<string> ContextExtras(string kind)
    {
        var manual = $"""SELECT count(*) FROM "CalendarEntries" m WHERE m."ListingKind" = '{kind}' AND m."ListingId" = l."Id" AND m."Kind" = 'Manual' AND m."DateTo" >= @today""";
        if (kind != ListingKinds.Stay) return [manual];
        return
        [
            manual,
            """SELECT count(*) FROM "StayBlockedRanges" b WHERE b."StayListingId" = l."Id" AND b."DateTo" >= @today""",
            """SELECT count(*) FROM "StaySpecialPrices" p WHERE p."StayListingId" = l."Id" AND p."DateTo" >= @today""",
        ];
    }

    // C1: entries touching [@from, @to], the manual rows above the plan limit too (InactiveByPlan, shown greyed with Delete only),
    // plus the visible manual rows past @windowEnd (the manual window end, FB-05: a Free row inside today + 15 is never "hidden")
    // for hiddenCount.
    public static readonly string Month = $"""
        SELECT COALESCE((SELECT json_agg(x ORDER BY x."DateFrom", x."Id") FROM (
                   SELECT {Columns(inactive: true)} FROM "CalendarEntries" c
                   WHERE c."ListingKind" = @k AND c."ListingId" = @l AND c."DateTo" >= @from AND c."DateFrom" <= @to) x), '[]')::text AS "Items",
               (SELECT count(*) FROM ({VisibleManualSet}) w WHERE w."DateFrom" > @windowEnd) AS "VisibleBeyond",
               NULL::bigint AS "AutoBeyond", NULL::date AS "AutoBeyondFirst"
        """;

    // C2: one keyset page of upcoming entries, plus every manual row above the plan limit (InactiveByPlan, Delete only) at any
    // date so it can be cleared; the counts are computed on the first page only. The first page has no row
    // comparison, a later page a plain one, so the cursor is an index condition (no "@cd IS NULL OR" form).
    public static readonly string Upcoming = UpcomingSql(afterCursor: false);

    public static readonly string UpcomingAfter = UpcomingSql(afterCursor: true);

    private static string UpcomingSql(bool afterCursor) => $"""
        SELECT COALESCE((SELECT json_agg(x ORDER BY x."DateFrom", x."Id") FROM (
                   SELECT {Columns(inactive: true)} FROM "CalendarEntries" c
                   WHERE c."ListingKind" = @k AND c."ListingId" = @l AND c."DateTo" >= @today
                     AND ((c."DateFrom" <= @upcomingTo AND {Visible()}) OR (c."Kind" = 'Manual' AND NOT {Visible()}))
                     {(afterCursor ? "AND (c.\"DateFrom\", c.\"Id\") > (@cd, @cid)" : "")}
                   ORDER BY c."DateFrom", c."Id" LIMIT @take) x), '[]')::text AS "Items",
               CASE WHEN @first THEN (SELECT count(*) FROM ({VisibleManualSet}) w WHERE w."DateFrom" > @manualTo) END AS "VisibleBeyond",
               CASE WHEN @first THEN (SELECT count(*) FROM "CalendarEntries" a
                    WHERE a."ListingKind" = @k AND a."ListingId" = @l AND a."Kind" = 'Auto' AND NOT a."Cancelled" AND a."DateFrom" > @upcomingTo) END AS "AutoBeyond",
               CASE WHEN @first THEN (SELECT min(a."DateFrom") FROM "CalendarEntries" a
                    WHERE a."ListingKind" = @k AND a."ListingId" = @l AND a."Kind" = 'Auto' AND NOT a."Cancelled" AND a."DateFrom" > @upcomingTo) END AS "AutoBeyondFirst"
        """;

    // C3: entries that ended in the last 90 days, newest first (backward scan of ix_calendarentries_listing_dateto).
    public static readonly string Past = PastSql(beforeCursor: false);

    public static readonly string PastBefore = PastSql(beforeCursor: true);

    private static string PastSql(bool beforeCursor) => $"""
        SELECT {Columns()} FROM "CalendarEntries" c
        WHERE c."ListingKind" = @k AND c."ListingId" = @l AND c."DateTo" < @today AND c."DateTo" >= @pastFrom
          {(beforeCursor ? "AND (c.\"DateTo\", c.\"Id\") < (@cd, @cid)" : "")}
        ORDER BY c."DateTo" DESC, c."Id" DESC LIMIT @take
        """;

    // The re-read after a 0-row conditional update (any row of the listing, hidden too).
    public static readonly string One = $"""
        SELECT {Columns(sourceAvailable: true)} FROM "CalendarEntries" c WHERE c."Id" = @id AND c."ListingKind" = @k AND c."ListingId" = @l
        """;

    // C4 (D-30 / FB-55): a manual entry hidden by the plan answers 404; Auto and past rows are always readable.
    public static readonly string OneVisible = $"""
        SELECT {Columns(sourceAvailable: true)} FROM "CalendarEntries" c WHERE c."Id" = @id AND c."ListingKind" = @k AND c."ListingId" = @l
          AND {Visible()}
        """;

    // C5 before the insert: the active count, the kept-rows ceiling count (D-07: past-dated rows too) and any row that
    // already holds the client id (replay or IdTaken).
    public static readonly string CreateCheck = $"""
        SELECT ({ActiveManualCount}) AS "Active",
               (SELECT count(*) FROM "CalendarEntries" m
                WHERE m."ListingKind" = @k AND m."ListingId" = @l AND m."Kind" = 'Manual' AND m."DateTo" >= @keptFrom) AS "Total",
               (SELECT row_to_json(x)::text FROM (SELECT {Columns()} FROM "CalendarEntries" c WHERE c."Id" = @id) x) AS "Existing"
        """;

    public static readonly string Insert = $"""
        INSERT INTO "CalendarEntries" AS c ("Id", "OwnerUserId", "ListingKind", "ListingId", "Kind", "DateFrom", "DateTo", "TimeOfDay",
            "FromPlace", "ToPlace", "Label", "Note", "FinalAmount", "AdvanceReceived", "PaidInFull", "Cancelled", "Version", "CreatedAt", "UpdatedAt")
        VALUES (@id, @me, @k, @l, 'Manual', @from, @to, @time, @fromPlace, @toPlace, @label, @note, @final, @advanceReceived, @paid, false, 1, now(), now())
        ON CONFLICT ("Id") DO NOTHING
        RETURNING {Columns()}
        """;

    public static readonly string Update = $"""
        UPDATE "CalendarEntries" AS c SET "DateFrom" = @from, "DateTo" = @to, "TimeOfDay" = @time, "FromPlace" = @fromPlace, "ToPlace" = @toPlace,
            "Label" = @label, "Note" = @note, "FinalAmount" = @final, "AdvanceReceived" = @advanceReceived, "PaidInFull" = @paid,
            "Version" = c."Version" + 1, "UpdatedAt" = now()
        WHERE c."Id" = @id AND c."ListingKind" = @k AND c."ListingId" = @l AND c."Kind" = 'Manual' AND c."Version" = @v
          AND c."DateTo" >= @today
          AND (@fromOk OR c."DateFrom" = @from) AND (@toOk OR c."DateTo" = @to)
          AND {Visible()}
        RETURNING {Columns()}
        """;

    public static readonly string Delete = """
        WITH d AS (DELETE FROM "CalendarEntries" WHERE "Id" = @id AND "ListingKind" = @k AND "ListingId" = @l AND "Kind" = 'Manual' RETURNING 1)
        SELECT (SELECT count(*) FROM d)::int AS "Deleted",
               EXISTS (SELECT 1 FROM "CalendarEntries" a WHERE a."Id" = @id AND a."ListingKind" = @k AND a."ListingId" = @l AND a."Kind" = 'Auto') AS "IsAuto"
        """;

    public static readonly string Money = $"""
        UPDATE "CalendarEntries" AS c SET "FinalAmount" = @final, "AdvanceReceived" = @advanceReceived, "PaidInFull" = @paid,
            "Version" = c."Version" + 1, "UpdatedAt" = now()
        WHERE c."Id" = @id AND c."ListingKind" = @k AND c."ListingId" = @l AND c."Version" = @v AND {Visible()}
        RETURNING {Columns()}
        """;

    // After a manual save: the active count for the meter and the overlap line. Stay nights are [from, to); a taxi trip is one day.
    public static string Overlap(string kind) => kind == ListingKinds.Stay
        ? $"""
          SELECT ({ActiveManualCount}) AS "Active",
                 (SELECT count(*) FROM "CalendarEntries" o WHERE o."ListingKind" = @k AND o."ListingId" = @l AND o."Id" <> @id
                    AND NOT o."Cancelled" AND o."DateFrom" < @to AND o."DateTo" > @from) AS "Bookings",
                 (SELECT count(*) FROM "StayBlockedRanges" b WHERE b."StayListingId" = @l AND b."DateFrom" < @to AND b."DateTo" >= @from) AS "Blocked"
          """
        : $"""
          SELECT ({ActiveManualCount}) AS "Active",
                 (SELECT count(*) FROM "CalendarEntries" o WHERE o."ListingKind" = @k AND o."ListingId" = @l AND o."Id" <> @id
                    AND NOT o."Cancelled" AND o."DateFrom" <= @to AND o."DateTo" >= @from) AS "Bookings",
                 0::bigint AS "Blocked"
          """;

    // C9: one grouped statement over the month (DateFrom in [@m1, @m2), not cancelled) plus the top 3 balances.
    public static readonly string Summary = $"""
        WITH m AS (
            SELECT {Columns()}, {MoneyNoteMath.SqlReceived("c")} AS "Rec", {MoneyNoteMath.SqlBalance("c")} AS "Bal"
            FROM "CalendarEntries" c
            WHERE c."ListingKind" = @k AND c."ListingId" = @l AND c."DateFrom" >= @m1 AND c."DateFrom" < @m2 AND NOT c."Cancelled")
        SELECT COALESCE(sum(m."FinalAmount"), 0)::bigint AS "FinalTotal",
               COALESCE(sum(m."Rec"), 0)::bigint AS "ReceivedTotal",
               COALESCE(sum(m."Bal"), 0)::bigint AS "BalanceTotal",
               count(*)::int AS "EntryCount",
               (count(*) FILTER (WHERE m."Bal" > 0))::int AS "DueCount",
               COALESCE((SELECT json_agg(t ORDER BY t."Bal" DESC, t."Id" DESC)
                         FROM (SELECT * FROM m WHERE m."Bal" > 0 ORDER BY m."Bal" DESC, m."Id" DESC LIMIT 3) t), '[]')::text AS "Top"
        FROM m
        """;

    // C10: entries of the month with a balance, largest first, keyset on (balance, id). FB-44: the same order as the top 3 of the
    // summary (Balance DESC, Id DESC), and two texts (first page / after a cursor) instead of an OR-null form the planner cannot use.
    private static readonly string BalanceDueBase = $"""
        SELECT {Columns()} FROM "CalendarEntries" c
        WHERE c."ListingKind" = @k AND c."ListingId" = @l AND c."DateFrom" >= @m1 AND c."DateFrom" < @m2 AND NOT c."Cancelled"
          AND c."FinalAmount" IS NOT NULL AND {MoneyNoteMath.SqlBalance("c")} > 0
        """;

    public static readonly string BalanceDue = BalanceDueBase + $"""
        ORDER BY {MoneyNoteMath.SqlBalance("c")} DESC, c."Id" DESC LIMIT @take
        """;

    public static readonly string BalanceDueAfter = BalanceDueBase + $"""
          AND ({MoneyNoteMath.SqlBalance("c")}, c."Id") < (@cb, @cid)
        ORDER BY {MoneyNoteMath.SqlBalance("c")} DESC, c."Id" DESC LIMIT @take
        """;

    public static string Reminder(string kind) => kind == ListingKinds.Stay
        ? """UPDATE "StayListings" SET "RemindersOn" = @on WHERE "Id" = @l"""
        : """UPDATE "TaxiListings" SET "RemindersOn" = @on WHERE "Id" = @l""";

    public const string Blocks = """
        SELECT b."Id", b."StayListingId", b."DateFrom", b."DateTo", b."Reason", b."Version" FROM "StayBlockedRanges" b
        WHERE b."StayListingId" = @l AND b."DateTo" >= @from AND b."DateFrom" <= @to
        ORDER BY b."DateFrom", b."Id"
        """;

    public const string SpecialPrices = """
        SELECT p."Id", p."DateFrom", p."DateTo", p."Direction", p."Percent"::int AS "Percent", p."LabelCopy" AS "Label", p."IsPaused", p."PausedByPlan"
        FROM "StaySpecialPrices" p
        WHERE p."StayListingId" = @l AND p."DateTo" >= @from AND p."DateFrom" <= @to
        ORDER BY p."DateFrom", p."Id"
        """;

    // K1: the stay's active blocks (at most the hard cap, no cursor).
    public const string ActiveBlocks = """
        SELECT b."Id", b."StayListingId", b."DateFrom", b."DateTo", b."Reason", b."Version" FROM "StayBlockedRanges" b
        WHERE b."StayListingId" = @l AND b."DateTo" >= @today
        ORDER BY b."DateFrom", b."Id"
        """;

    // K2 / K3 under the stay lock: active count, overlap with another block, confirmed and open bookings inside, the row of @id.
    public static readonly string BlockCheck = $"""
        SELECT (SELECT count(*) FROM "StayBlockedRanges" b WHERE b."StayListingId" = @l AND b."DateTo" >= @today) AS "Active",
               EXISTS (SELECT 1 FROM "StayBlockedRanges" b WHERE b."StayListingId" = @l AND b."Id" <> @id AND b."DateFrom" <= @to AND b."DateTo" >= @from) AS "Overlap",
               (SELECT count(*) FROM "TripEnquiries" e WHERE e."OwnerUserId" = @me AND e."Kind" = '{TripEnquiryKinds.Stay}' AND e."ListingId" = @l
                  AND e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND e."CheckIn" <= @to AND e."CheckOut" > @from) AS "ConfirmedInside",
               (SELECT count(*) FROM "TripEnquiries" e WHERE e."OwnerUserId" = @me AND e."Kind" = '{TripEnquiryKinds.Stay}' AND e."ListingId" = @l
                  AND e."Status" IN ({string.Join(", ", TripEnquiryStatuses.Unconfirmed.Select(s => $"'{s}'"))}) AND e."CheckIn" <= @to AND e."CheckOut" > @from) AS "OpenInside",
               (SELECT row_to_json(x)::text FROM (SELECT b."Id", b."StayListingId", b."DateFrom", b."DateTo", b."Reason", b."Version"
                                                   FROM "StayBlockedRanges" b WHERE b."Id" = @id) x) AS "Existing"
        """;

    public const string BlockInsert = """
        INSERT INTO "StayBlockedRanges" ("Id", "StayListingId", "DateFrom", "DateTo", "Reason", "Version", "CreatedAt", "UpdatedAt")
        VALUES (@id, @l, @from, @to, @reason, 1, now(), now())
        ON CONFLICT ("Id") DO NOTHING
        RETURNING "Id", "StayListingId", "DateFrom", "DateTo", "Reason", "Version"
        """;

    public const string BlockUpdate = """
        UPDATE "StayBlockedRanges" AS b SET "DateFrom" = @from, "DateTo" = @to, "Reason" = @reason, "Version" = b."Version" + 1, "UpdatedAt" = now()
        WHERE b."Id" = @id AND b."StayListingId" = @l AND b."Version" = @v
        RETURNING b."Id", b."StayListingId", b."DateFrom", b."DateTo", b."Reason", b."Version"
        """;

    // FB-36: one statement deletes his row and tells whether the id belongs to another stay (404); a gone id (his own retry) is 204.
    public const string BlockDelete = """
        WITH del AS (DELETE FROM "StayBlockedRanges" WHERE "Id" = @id AND "StayListingId" = @l RETURNING 1)
        SELECT (SELECT count(*) FROM del)::int AS "Deleted",
               EXISTS (SELECT 1 FROM "StayBlockedRanges" o WHERE o."Id" = @id AND o."StayListingId" <> @l) AS "Foreign"
        """;

    public static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter Uuid(string name, Guid? value) => new(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };

    public static NpgsqlParameter Date(string name, DateOnly? value) => new(name, NpgsqlDbType.Date) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter Int(string name, int? value) => new(name, NpgsqlDbType.Integer) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Long(string name, long? value) => new(name, NpgsqlDbType.Bigint) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Small(string name, short? value) => new(name, NpgsqlDbType.Smallint) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Bool(string name, bool value) => new(name, NpgsqlDbType.Boolean) { Value = value };

    public static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Jsonb(string name, string? value) => new(name, NpgsqlDbType.Jsonb) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter Time(string name, TimeOnly? value) => new(name, NpgsqlDbType.Time) { Value = (object?)value ?? DBNull.Value };

    public static NpgsqlParameter[] Scope(string kind, Guid listingId) => [Text("k", kind), Uuid("l", listingId)];
}

public sealed class CalendarListRow
{
    public string Items { get; set; } = "[]";
    public long? VisibleBeyond { get; set; }
    public long? AutoBeyond { get; set; }
    public DateOnly? AutoBeyondFirst { get; set; }
}

public sealed class CalendarCreateCheckRow
{
    public long Active { get; set; }
    public long Total { get; set; }
    public string? Existing { get; set; }
}

public sealed class CalendarDeleteRow
{
    public int Deleted { get; set; }
    public bool IsAuto { get; set; }
}

public sealed class CalendarOverlapRow
{
    public long Active { get; set; }
    public long Bookings { get; set; }
    public long Blocked { get; set; }
}

public sealed class CalendarSummaryRow
{
    public long FinalTotal { get; set; }
    public long ReceivedTotal { get; set; }
    public long BalanceTotal { get; set; }
    public int EntryCount { get; set; }
    public int DueCount { get; set; }
    public string Top { get; set; } = "[]";
}

public sealed class FestivalRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
}

public sealed class BlockedRangeRow
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? Reason { get; set; }
    public int Version { get; set; }
}

public sealed class CalendarSpecialPriceRow
{
    public Guid Id { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Direction { get; set; } = string.Empty;
    public int Percent { get; set; }
    public string? Label { get; set; }
    public bool IsPaused { get; set; }
    public bool PausedByPlan { get; set; }
}

public sealed class BlockCheckRow
{
    public long Active { get; set; }
    public bool Overlap { get; set; }
    public long ConfirmedInside { get; set; }
    public long OpenInside { get; set; }
    public string? Existing { get; set; }
}

// 404 of a row id (entry or block) that is not the caller's; the handler answers NotFoundResponse.
public static class Be1ErrorTypes
{
    public const string NotFound = "NotFound";
}

// A refusal of a BE1 service: the error type, its message and data; StaleEdit carries the stored row for the handler to map.
public sealed record Be1Error(string Type, string Message, object? Data = null, CalendarEntryRow? CurrentEntry = null, BlockedRangeRow? CurrentBlock = null)
{
    public static Be1Error Of(string type, object? data = null) => new(type, OwnerErrorTypes.MessageOf(type), data);
}

public sealed record Be1Result<T>(T? Value, Be1Error? Error)
{
    public bool Ok => Error == null;

    public static implicit operator Be1Result<T>(Be1Error error) => new(default, error);

    public static Be1Result<T> Success(T value) => new(value, null);
}

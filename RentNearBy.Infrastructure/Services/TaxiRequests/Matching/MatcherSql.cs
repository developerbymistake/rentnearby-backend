using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

// SQL of the matcher. It reads the city key columns only (DispatchCityKey, MatchToCityKey, MatchFromCityKey and, through
// TaxiCandidateQuery, TaxiRoutes.FromCityKey / ToCityKey); how a key was filled is never looked at here.
public static class MatcherSql
{
    public const int RequestLimit = 100;
    // Every lock of a pass is a try lock or SKIP LOCKED except the offer insert and the request end; this bounds those.
    // Hitting it ends the pass as LockBusy (TaxiMatcherPass), so it stays short.
    public const int LockTimeoutMs = 250;

    private static readonly string Dispatching = string.Join(", ", RequestStates.Dispatching.Select(s => $"'{s}'"));

    private static readonly string RingingOf = $"""
        (SELECT count(*) FROM "TaxiRequestOffers" o WHERE o."RequestId" = r."Id" AND o."Status" = '{OfferStates.Ringing}')
        """;

    // Mirrors TaxiFairQueue.RingsNeeded: min(3, target - accepted) - ringing > 0.
    private static readonly string NeedsRings = $"""LEAST({TaxiDispatchRules.MaxLiveRings}, r."AcceptTarget" - r."AcceptedCount") > {RingingOf}""";

    public static readonly string SetLockTimeout = $"SET LOCAL lock_timeout = '{LockTimeoutMs}ms'";

    public const string TryLockCity = """
        SELECT pg_try_advisory_xact_lock(hashtextextended('taxi-match:' || @city::text, 0)) AS "Value"
        """;

    // Only matcher passes insert Ringing offers; a driver held by another pass is skipped, never waited for.
    public const string TryLockDrivers = """
        SELECT d AS "Value" FROM unnest(@ids) AS d
        WHERE pg_try_advisory_xact_lock(hashtextextended('taxi-ring:' || d::text, 0))
        """;

    // Same key, waiting form: a select takes it before it moves an offer to Selected, so a ring insert and a selection of
    // one driver never interleave (decision 7). The ring insert then re-checks for a Selected offer in its own statement.
    public const string LockDriverRing = """
        SELECT pg_advisory_xact_lock(hashtextextended('taxi-ring:' || @driver::text, 0))
        """;

    public const string Now = "SELECT now() AS \"Value\"";

    public const string EnsureCity = """
        INSERT INTO "TaxiDispatchCities" ("CityKey", "Seq", "LastMinPass", "UpdatedAt") VALUES (@city, 0, 0, now())
        ON CONFLICT ("CityKey") DO NOTHING
        """;

    public const string LockCity = """
        SELECT "Seq", "LastMinPass" FROM "TaxiDispatchCities" WHERE "CityKey" = @city FOR UPDATE SKIP LOCKED
        """;

    public static readonly string LoadRequests = $"""
        SELECT r."Id", r."TripId", r."TravellerUserId", r."CreatedAt",
               r."AcceptTarget"::int AS "AcceptTarget", r."AcceptedCount"::int AS "AcceptedCount", {RingingOf}::int AS "Ringing",
               r."Seats"::int AS "Seats", r."MatchToCityKey", r."MatchFromCityKey", r."DateFrom", r."DateTo",
               r."QueueWaitStartedAt", r."QueueExhaustedAt"
        FROM "TaxiRequests" r
        WHERE r."DispatchCityKey" = @city AND r."Status" IN ({Dispatching})
        AND {NeedsRings}
        ORDER BY r."CreatedAt", r."Id"
        LIMIT @limit
        FOR UPDATE OF r SKIP LOCKED
        """;

    public static readonly string InsertOffer = $"""
        INSERT INTO "TaxiRequestOffers" ("Id", "RequestId", "DriverListingId", "DriverUserId", "Status", "SeqNo", "OfferedAt", "ExpiresAt", "PushLate")
        SELECT @id, @requestId, @driverId, @driverUserId, '{OfferStates.Ringing}',
               COALESCE((SELECT max(x."SeqNo") FROM "TaxiRequestOffers" x WHERE x."RequestId" = @requestId), 0) + 1,
               now(), now() + interval '{TaxiDispatchRules.RingSeconds} seconds', false
        WHERE NOT EXISTS (SELECT 1 FROM "TaxiRequestOffers" s WHERE s."DriverListingId" = @driverId AND s."Status" = '{OfferStates.Selected}')
        ON CONFLICT ("DriverListingId") WHERE "Status" = '{OfferStates.Ringing}' DO NOTHING
        RETURNING "Id" AS "OfferId", "ExpiresAt"
        """;

    public const string UpdateStates = """
        UPDATE "DriverDispatchStates" AS ds SET "Pass" = u."Pass", "SeenSeq" = u."SeenSeq", "LastOfferedAt" = u."LastOfferedAt"
        FROM unnest(@ids, @passes, @seens, @lasts) AS u("Id", "Pass", "SeenSeq", "LastOfferedAt")
        WHERE ds."CityKey" = @city AND ds."DriverListingId" = u."Id"
        """;

    public const string UpdateCity = """
        UPDATE "TaxiDispatchCities" SET "Seq" = @seq, "LastMinPass" = @minPass, "UpdatedAt" = now() WHERE "CityKey" = @city
        """;

    public static readonly string MarkServed = $"""
        UPDATE "TaxiRequests" SET "Version" = "Version" + 1, "UpdatedAt" = now(), "QueueWaitStartedAt" = NULL, "QueueExhaustedAt" = NULL
        WHERE "Id" = @id AND "Status" IN ({Dispatching})
        RETURNING "Version", "Status", "WaitRound", "QueueWaitStartedAt"
        """;

    // Same rule as StartWait: a stored wait counts only until its 10 minutes are over.
    public static bool WaitRunning(DateTime? waitStartedAt, DateTime nowUtc)
        => waitStartedAt is { } started && started.AddSeconds(TaxiDispatchRules.QueueWaitSeconds) > nowUtc;

    public static readonly string StartWait = $"""
        UPDATE "TaxiRequests" SET "QueueWaitStartedAt" = now(), "WaitRound" = "WaitRound" + 1, "Version" = "Version" + 1, "UpdatedAt" = now()
        WHERE "Id" = @id AND "Status" IN ({Dispatching})
          AND ("QueueWaitStartedAt" IS NULL OR "QueueWaitStartedAt" + interval '{TaxiDispatchRules.QueueWaitSeconds} seconds' <= now())
        RETURNING "Version", "Status", "WaitRound", "QueueWaitStartedAt"
        """;

    public static readonly string MarkExhausted = $"""
        UPDATE "TaxiRequests" SET "QueueExhaustedAt" = now(), "Version" = "Version" + 1, "UpdatedAt" = now()
        WHERE "Id" = @id AND "Status" IN ({Dispatching}) AND "QueueExhaustedAt" IS NULL
        RETURNING "Version", "Status", "WaitRound", "QueueWaitStartedAt"
        """;

    // Sweeper: every city with a request that still needs rings (a waiting request needs them by definition). An exhausted
    // request waits for ask more or a reopen, which clear the mark and signal the city.
    public static readonly string DueCities = $"""
        SELECT DISTINCT r."DispatchCityKey" AS "Value" FROM "TaxiRequests" r
        WHERE r."Status" IN ({Dispatching}) AND r."QueueExhaustedAt" IS NULL
        AND {NeedsRings}
        """;

    // Cities a freed driver can matter to: his dispatch state rows, and the dispatch cities of requests his routes serve.
    public static readonly string CitiesOfDrivers = $"""
        SELECT ds."CityKey" AS "Value" FROM "DriverDispatchStates" ds WHERE ds."DriverListingId" = ANY(@ids)
        UNION
        SELECT q."DispatchCityKey" AS "Value" FROM "TaxiRoutes" rt
        JOIN "TaxiRequests" q ON q."MatchToCityKey" = rt."ToCityKey" AND q."Status" IN ({Dispatching})
        WHERE rt."TaxiListingId" = ANY(@ids)
        """;

    // Contract AD health: Open or Collecting, no ringing offer, no accepted driver, no pending wait, for over 15 minutes.
    public static readonly string CountStuck = $"""
        SELECT count(*)::int AS "Value" FROM "TaxiRequests" r
        WHERE r."Status" IN ({Dispatching}) AND r."QueueWaitStartedAt" IS NULL AND r."UpdatedAt" < now() - interval '15 minutes'
        AND NOT EXISTS (SELECT 1 FROM "TaxiRequestOffers" o WHERE o."RequestId" = r."Id"
                        AND o."Status" IN ('{OfferStates.Ringing}', '{OfferStates.Accepted}'))
        """;
}

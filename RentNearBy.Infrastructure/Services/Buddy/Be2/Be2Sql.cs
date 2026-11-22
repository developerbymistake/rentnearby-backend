using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// The trip row as the lock statement returns it (re-read after the wait, so the values are current).
internal sealed class LockedTripRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public string OwnerName { get; set; } = string.Empty;
}

// One invite row with its trip and owner (S7, S8, S9).
internal sealed class InviteRow
{
    public Guid MemberId { get; set; }
    public Guid TripId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime InviteExpiresAt { get; set; }
    public int Version { get; set; }
    public string InviteeName { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string? OwnerUsername { get; set; }
    public string? OwnerPhotoUrl { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public string PlanJson { get; set; } = "{}";
    public string CityName { get; set; } = string.Empty;
    public bool Blocked { get; set; }
    public int JoinedViewers { get; set; }
}

internal static class Be2Sql
{
    public const string Joined = TripMemberStatuses.Joined;
    public const string Invited = TripMemberStatuses.Invited;

    // Trip row lock (contract 5.1 step 3) that also returns the row; counted like any statement.
    private const string LockTripSql = """
        SELECT t."Id", t."OwnerUserId", t."Title", t."Status", t."StartDate", t."EndDate", t."DayCount", t."SpotCount",
               COALESCE(u."Name", '') AS "OwnerName"
        FROM "Trips" t JOIN "Users" u ON u."Id" = t."OwnerUserId"
        WHERE t."Id" = @trip
        FOR UPDATE OF t
        """;

    // A silent decline keeps looking like a waiting invite to the owner (doc Part 3 Q5) while S2/S17 still treat it as invited.
    public static readonly string LooksWaitingWhere = LooksWaiting("w");

    public static string LooksWaiting(string a) => $"""
        (({a}."Status" = 'Invited' AND {a}."InviteExpiresAt" > now())
          OR ({a}."Status" = 'Declined' AND {a}."InviteExpiresAt" > now()
              AND {a}."RespondedAt" > now() - make_interval(secs => {(long)BuddyLimits.ReinviteAfterDecline.TotalSeconds})))
        """;

    private const string InviteSelect = """
        SELECT m."Id" AS "MemberId", m."TripId", m."UserId", m."Status", m."InviteExpiresAt", m."Version",
               COALESCE(iu."Name", '') AS "InviteeName",
               t."OwnerUserId", COALESCE(o."Name", '') AS "OwnerName", o."Username" AS "OwnerUsername", o."ProfilePhotoUrl" AS "OwnerPhotoUrl",
               t."Title", t."StartDate", t."EndDate", t."DayCount",
        """;

    private const string InviteRest = """
               EXISTS (SELECT 1 FROM "UserBlocks" k
                       WHERE (k."BlockerId" = m."UserId" AND k."BlockedId" = t."OwnerUserId")
                          OR (k."BlockerId" = t."OwnerUserId" AND k."BlockedId" = m."UserId")) AS "Blocked",
               (SELECT count(*)::int FROM "TripMembers" j WHERE j."TripId" = m."TripId" AND j."Status" = 'Joined') AS "JoinedViewers"
        FROM "TripMembers" m
        JOIN "Trips" t ON t."Id" = m."TripId"
        JOIN "Users" o ON o."Id" = t."OwnerUserId"
        JOIN "Users" iu ON iu."Id" = m."UserId"
        WHERE m."Id" = @member
        """;

    // S7 needs the leg cities; S8 and S9 never read the plan.
    public const string InviteSql = InviteSelect + "\n" + """
               t."PlanJson"::text AS "PlanJson", t."CityName",
        """ + "\n" + InviteRest;

    public const string InviteLeanSql = InviteSelect + "\n" + """
               '{}'::text AS "PlanJson", ''::text AS "CityName",
        """ + "\n" + InviteRest;

    // Deletes the TripInvite notices of (user, trip) pairs in one statement (block participant and expiry job).
    public const string DeleteInviteNoticesSql = """
        DELETE FROM "UserNotices" n
        USING unnest(@users, @trips) AS x(u, t)
        WHERE n."UserId" = x.u AND n."TripId" = x.t AND n."Kind" = 'TripInvite'
        """;

    public static NpgsqlParameter Id(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter Ids(string name, IEnumerable<Guid> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    public static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };

    public static NpgsqlParameter Time(string name, DateTime value) => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter Bool(string name, bool value) => new(name, NpgsqlDbType.Boolean) { Value = value };

    public static async Task<LockedTripRow?> LockTripAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<LockedTripRow>(LockTripSql, Id("trip", tripId)).ToListAsync(ct)).FirstOrDefault();

    public static async Task<InviteRow?> ReadInviteAsync(ApplicationDbContext db, Guid memberId, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<InviteRow>(InviteSql, Id("member", memberId)).ToListAsync(ct)).FirstOrDefault();

    public static async Task<InviteRow?> ReadInviteLeanAsync(ApplicationDbContext db, Guid memberId, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<InviteRow>(InviteLeanSql, Id("member", memberId)).ToListAsync(ct)).FirstOrDefault();

    public static Task DeleteInviteNoticesAsync(ApplicationDbContext db, IReadOnlyList<(Guid UserId, Guid TripId)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0) return Task.CompletedTask;
        return db.Database.ExecuteSqlRawAsync(DeleteInviteNoticesSql,
            [Ids("users", pairs.Select(p => p.UserId)), Ids("trips", pairs.Select(p => p.TripId))], ct);
    }

    // S14/S15 read-only copies for the pure row rules: only the columns those rules read (P6). The left-out enquiry columns
    // (ContextRefreshCount, AcceptedOfferId, StepKey, ClientRequestId, RemindedOn, FinalPushedAt,
    // CancelledByUserId, CancelReason, ConfirmedByTravellerAt, ConfirmedByOwnerAt, CancelledAt) are not
    // read by TripViewerProjection, TripEasyRows or the rules they call.
    public static IQueryable<RentNearBy.Core.Entities.TripEnquiry> ViewerEnquiries(IQueryable<RentNearBy.Core.Entities.TripEnquiry> q)
        => q.AsNoTracking().Select(e => new RentNearBy.Core.Entities.TripEnquiry
        {
            Id = e.Id, TripId = e.TripId, TravellerUserId = e.TravellerUserId, OwnerUserId = e.OwnerUserId, Kind = e.Kind,
            ListingId = e.ListingId, ListingName = e.ListingName, ListingLatitude = e.ListingLatitude, ListingLongitude = e.ListingLongitude,
            Day = e.Day, DaysJson = e.DaysJson, CityLeg = e.CityLeg, CheckIn = e.CheckIn, CheckOut = e.CheckOut,
            ConversationId = e.ConversationId, ContextJson = e.ContextJson, Status = e.Status, FailReason = e.FailReason,
            ExpiresAt = e.ExpiresAt, IsUrgent = e.IsUrgent, AgreedTotal = e.AgreedTotal, DriverFareEditCount = e.DriverFareEditCount,
            ListedRateSnapshot = e.ListedRateSnapshot, DriverFare = e.DriverFare, AgreedFare = e.AgreedFare, DriverNote = e.DriverNote,
            DriverRepliedAt = e.DriverRepliedAt, BookedByTravellerAt = e.BookedByTravellerAt, TollExtra = e.TollExtra,
            PickupTime = e.PickupTime, PickupPlace = e.PickupPlace, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt,
        });

    public static IQueryable<RentNearBy.Core.Entities.Trip> ViewerTrip(IQueryable<RentNearBy.Core.Entities.Trip> q)
        => q.AsNoTracking().Select(t => new RentNearBy.Core.Entities.Trip
        {
            Id = t.Id, OwnerUserId = t.OwnerUserId, Title = t.Title, Status = t.Status, CityKey = t.CityKey, CityName = t.CityName,
            CityLatitude = t.CityLatitude, CityLongitude = t.CityLongitude, DistrictId = t.DistrictId, PlanJson = t.PlanJson,
            DayCount = t.DayCount, SpotCount = t.SpotCount, StartDate = t.StartDate, EndDate = t.EndDate, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
        });

    public static List<string> LegCities(string planJson, string cityName)
    {
        var plan = RentNearBy.Core.Models.TripPlanReader.Parse(planJson);
        var cities = plan.Legs is { Count: > 0 } legs ? legs.Where(l => l != null).Select(l => l.CityName).ToList() : [];
        return cities.Count > 0 ? cities : [cityName];
    }
}

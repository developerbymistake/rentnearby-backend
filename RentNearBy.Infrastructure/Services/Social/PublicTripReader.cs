using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Reads of public trips (contract 2.4) with the month-only projection. Spec 22: every live public trip is visible to every
// viewer whatever its join gender; notOpen only tells him he cannot request it (wrong gender). One statement per list plus
// one grouped mutuals statement.
public sealed class PublicTripReader(ApplicationDbContext db, TimeProvider clock)
{
    // A live post (contract 1.5). Needs @today (IST date). The owner side (active seeker with a profile) is the joins of SelectSql.
    public static string LivePost(string t) =>
        $"""{t}."IsPublic" AND {t}."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') AND ({t}."StartDate" IS NULL OR {t}."StartDate" >= @today) AND ({t}."PostWindowMonths" IS NULL OR {t}."PublishedAt" + make_interval(months => {t}."PostWindowMonths"::int) >= now())""";

    // What a non-owner viewer may see: live, not his, no block, private owner only when he follows (any join gender).
    private static readonly string ViewerFilter = $"""
        {LivePost("t")} AND t."OwnerUserId" <> @me
        AND NOT {BlockedEitherWay("@me", "t.\"OwnerUserId\"")}
        AND (NOT op."IsPrivate" OR {AcceptedFollow("@me", "t.\"OwnerUserId\"")})
        """;

    private static string SelectSql(string where, string order = """ORDER BY t."PublishedAt" DESC, t."Id" DESC""") => $"""
        SELECT t."Id" AS "TripId", t."OwnerUserId", COALESCE(o."Name", '') AS "OwnerName", o."Username" AS "OwnerUsername",
               o."ProfilePhotoUrl" AS "OwnerPhoto", op."HomeCityName" AS "OwnerHomeCity", op."Gender" AS "OwnerGender", op."Interests" AS "OwnerInterests",
               o."CompletedTripsCount" AS "OwnerCompletedTrips",
               op."IsPrivate" AS "OwnerPrivate", t."CityKey", t."PostCityNames" AS "CityNames", t."PostStateName" AS "StateName",
               t."StartDate", t."PostStartMonth", t."PostWindowMonths", t."PublishedAt", t."PostDays", t."JoinGender", t."Seats",
               COALESCE(t."PostText", '') AS "PostText", t."IsPublic",
               (SELECT count(*) FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}')::int AS "Joined",
               EXISTS (SELECT 1 FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}') AS "MyMember",
               EXISTS (SELECT 1 FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."RequesterId" = @me AND r."Status" = '{JoinRequestStatuses.Waiting}') AS "MyWaiting",
               EXISTS (SELECT 1 FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."RequesterId" = @me AND r."Status" = '{JoinRequestStatuses.Declined}'
                       AND r."DecidedAt" > now() - {Interval(SocialLimits.AskAgainAfter)}) AS "MyDeclined",
               CASE WHEN t."OwnerUserId" = @me THEN (SELECT count(*) FROM "TripJoinRequests" r WHERE r."TripId" = t."Id" AND r."Status" = '{JoinRequestStatuses.Waiting}')::int END AS "Waiting",
               {AcceptedFollow("@me", "t.\"OwnerUserId\"")} AS "IFollowOwner",
               {BlockedEitherWay("@me", "t.\"OwnerUserId\"")} AS "Blocked",
               ({LivePost("t")}) AS "Live"
        FROM "Trips" t
        JOIN "Users" o ON o."Id" = t."OwnerUserId" AND {ActiveSeeker("o")}
        JOIN "UserProfiles" op ON op."UserId" = t."OwnerUserId"
        WHERE {where}
        {order}
        LIMIT @take
        """;

    private const string FeedQuery = """
         AND (@q = '' OR EXISTS (SELECT 1 FROM unnest(t."PostCityNames") c WHERE lower(c) LIKE @q) OR lower(COALESCE(t."PostStateName", '')) LIKE @q)
        """;

    // Feed filter 'Who can join' (who=anyone|women|men): only posts with exactly that join rule.
    private const string WhoFilter = """ AND t."JoinGender" = @who""";

    private static readonly string NearMe = $"""
         AND ST_DWithin(ST_SetSRID(ST_MakePoint(t."CityLongitude", t."CityLatitude"), 4326)::geography,
                        ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography, {SocialLimits.NearMeKm * 1000})
        """;

    private const string Keyset = """ AND (t."PublishedAt", t."Id") < (@at, @id)""";

    private static readonly string RailFilter = ViewerFilter + $"""
         AND NOT EXISTS (SELECT 1 FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}')
         AND (SELECT count(*) FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}') < t."Seats"
        """;

    private static readonly string MineFilter = $"""{LivePost("t")} AND t."OwnerUserId" = @me""";

    private static readonly string OwnerFilter = $"""{LivePost("t")} AND t."OwnerUserId" = @owner""";

    private const string ByIdFilter = """t."Id" = @trip""";

    private static readonly string ViewerGenderSql = """SELECT p."Gender" AS "Value" FROM "UserProfiles" p WHERE p."UserId" = @me""";

    public async Task<string?> ViewerGenderAsync(Guid me, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<string>(ViewerGenderSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();

    // whoJoinGender: null = every post, else one JoinGenders value (A / M / F) picked in the feed filter.
    public async Task<TripFeedDto> FeedAsync(Guid me, (DateTime At, Guid Id)? cursor, int limit, string? q, string? whoJoinGender,
        (double Lat, double Lng)? near, CancellationToken ct)
    {
        var gender = await ViewerGenderAsync(me, ct);
        var where = ViewerFilter + FeedQuery + (whoJoinGender is null ? string.Empty : WhoFilter) + (near is null ? string.Empty : NearMe) + (cursor is null ? string.Empty : Keyset);
        var ps = Base(me, limit + 1);
        ps.Add(Text("q", string.IsNullOrWhiteSpace(q) ? string.Empty : LikeContains(q)));
        if (whoJoinGender is not null) ps.Add(Text("who", whoJoinGender));
        if (near is { } n)
        {
            ps.Add(Double("lat", n.Lat));
            ps.Add(Double("lng", n.Lng));
        }
        if (cursor is { } c)
        {
            ps.Add(At("at", c.At));
            ps.Add(Uuid("id", c.Id));
        }
        var rows = await db.Database.SqlQueryRaw<Row>(SelectSql(where), ps.ToArray()).ToListAsync(ct);
        List<Row> mine = cursor is null ? await db.Database.SqlQueryRaw<Row>(SelectSql(MineFilter), Base(me, SocialLimits.PageMax).ToArray()).ToListAsync(ct) : [];
        var page = rows.Take(limit).ToList();
        var mutuals = await MutualsAsync(db, me, page.Select(r => r.OwnerUserId).ToList(), ct);
        var now = Now();
        return new TripFeedDto
        {
            Mine = mine.Select(r => ToDto(r, me, now, null, gender)).ToList(),
            Items = page.Select(r => ToDto(r, me, now, mutuals.GetValueOrDefault(r.OwnerUserId), gender)).ToList(),
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.PublishedAt ?? DateTime.MinValue, r => r.TripId),
        };
    }

    public async Task<TripRailDto> RailAsync(Guid me, CancellationToken ct)
    {
        var gender = await ViewerGenderAsync(me, ct);
        var rows = await db.Database.SqlQueryRaw<Row>(SelectSql(RailFilter), Base(me, SocialLimits.RailMax).ToArray()).ToListAsync(ct);
        if (rows.Count == 0) return new TripRailDto { Items = SocialSamples.Rail() };
        var mutuals = await MutualsAsync(db, me, rows.Select(r => r.OwnerUserId).ToList(), ct);
        var now = Now();
        return new TripRailDto { Items = rows.Select(r => ToDto(r, me, now, mutuals.GetValueOrDefault(r.OwnerUserId), gender)).ToList() };
    }

    // null = 404 TripPostNotFound (not public, not live, blocked, private owner not followed). Wrong gender = the full post with notOpen.
    public async Task<PublicTripDto?> ByIdAsync(Guid me, Guid tripId, CancellationToken ct)
    {
        var gender = await ViewerGenderAsync(me, ct);
        var ps = Base(me, 1);
        ps.Add(Uuid("trip", tripId));
        var r = (await db.Database.SqlQueryRaw<Row>(SelectSql(ByIdFilter), ps.ToArray()).ToListAsync(ct)).FirstOrDefault();
        if (r is null || !r.IsPublic) return null;
        var isMine = r.OwnerUserId == me;
        if (!isMine && (!r.Live || r.Blocked || (r.OwnerPrivate && !r.IFollowOwner))) return null;
        var mutuals = (await MutualsAsync(db, me, [r.OwnerUserId], ct)).GetValueOrDefault(r.OwnerUserId);
        return ToDto(r, me, Now(), mutuals, gender);
    }

    // Live posts of one owner as the viewer may see them (profile screens).
    public async Task<List<PublicTripDto>> OfOwnerAsync(Guid me, Guid owner, CancellationToken ct)
    {
        var gender = await ViewerGenderAsync(me, ct);
        var ps = Base(me, SocialLimits.PageMax);
        ps.Add(Uuid("owner", owner));
        var rows = await db.Database.SqlQueryRaw<Row>(SelectSql(OwnerFilter), ps.ToArray()).ToListAsync(ct);
        var now = Now();
        return rows.Select(r => ToDto(r, me, now, null, gender)).ToList();
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private List<NpgsqlParameter> Base(Guid me, int take) =>
    [
        Uuid("me", me), Int("take", take), Date("today", IstTime.TodayIst(Now())),
    ];

    // viewerGender = the viewer's profile gender (null without a profile). notOpen = not his post and his gender may not
    // request it (spec 22); nothing of the post is blanked.
    public static PublicTripDto ToDto(Row r, Guid me, DateTime nowUtc, MutualsDto? mutuals, string? viewerGender)
    {
        var isMine = r.OwnerUserId == me;
        var notOpen = !isMine && !SocialRules.CanJoin(r.JoinGender, viewerGender);
        var when = SocialRules.When(r.StartDate, r.PostStartMonth, r.PostWindowMonths, r.PublishedAt, nowUtc);
        var seats = r.Seats ?? 0;
        var dto = new PublicTripDto
        {
            TripId = r.TripId,
            Owner = WithLevel(Person(r.OwnerUserId, r.OwnerName, r.OwnerUsername, r.OwnerPhoto,
                r.OwnerPrivate && !r.IFollowOwner && !isMine ? null : r.OwnerHomeCity, r.OwnerGender), r.OwnerCompletedTrips),
            OwnerInterests = (r.OwnerInterests ?? []).Take(SocialLimits.OwnerInterestsShown).ToList(),
            IsMine = isMine,
            CityKey = r.CityKey,
            CityNames = (r.CityNames ?? []).ToList(),
            StateName = r.StateName,
            When = when.Kind == SocialRules.WhenMonth
                ? new WhenDto { Kind = SocialRules.WhenMonth, Month = when.Value }
                : new WhenDto { Kind = SocialRules.WhenWithin, Months = when.Value },
            Days = r.PostDays ?? 0,
            JoinGender = r.JoinGender,
            Seats = seats,
            SeatsLeft = SocialRules.SeatsLeft(seats, r.Joined),
            Text = r.PostText,
            PublishedAt = Utc(r.PublishedAt ?? DateTime.MinValue),
            MyJoin = r.MyMember ? MyJoinStates.Member : r.MyWaiting ? MyJoinStates.Requested : r.MyDeclined ? MyJoinStates.Declined : MyJoinStates.None,
            WaitingRequests = isMine ? r.Waiting ?? 0 : null,
            IFollowOwner = r.IFollowOwner,
            Mutuals = mutuals is { Count: > 0 } ? mutuals : null,
            NotOpen = notOpen,
        };
        return dto;
    }

    public sealed class Row
    {
        public Guid TripId { get; set; }
        public Guid OwnerUserId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string? OwnerUsername { get; set; }
        public string? OwnerPhoto { get; set; }
        public string? OwnerHomeCity { get; set; }
        public string? OwnerGender { get; set; }
        public string[]? OwnerInterests { get; set; }
        public int OwnerCompletedTrips { get; set; }
        public bool OwnerPrivate { get; set; }
        public long CityKey { get; set; }
        public string[]? CityNames { get; set; }
        public string? StateName { get; set; }
        public DateOnly? StartDate { get; set; }
        public int? PostStartMonth { get; set; }
        public short? PostWindowMonths { get; set; }
        public DateTime? PublishedAt { get; set; }
        public short? PostDays { get; set; }
        public string JoinGender { get; set; } = JoinGenders.Anyone;
        public short? Seats { get; set; }
        public string PostText { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public int Joined { get; set; }
        public bool MyMember { get; set; }
        public bool MyWaiting { get; set; }
        public bool MyDeclined { get; set; }
        public int? Waiting { get; set; }
        public bool IFollowOwner { get; set; }
        public bool Blocked { get; set; }
        public bool Live { get; set; }
    }
}

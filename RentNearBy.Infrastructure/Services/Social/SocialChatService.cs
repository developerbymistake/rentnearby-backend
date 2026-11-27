using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 2.6, 3.4: 1-to-1 and trip chat rooms on the ChatRooms tables. Every statement issued for a user carries the
// membership predicate (MemberOf); the hub methods also carry the live seeker predicate (SeekerSql).
public sealed class SocialChatService(
    ApplicationDbContext db, ISeekerLimits seekerLimits, INoticeWriter notices, SocialAfterCommit afterCommit,
    TimeProvider clock, ILogger<SocialChatService> logger) : ISocialChatService
{
    // The caller is a live member of room {room}.
    public static string MemberOf(string room) =>
        $"""EXISTS (SELECT 1 FROM "ChatRoomMembers" mo WHERE mo."RoomId" = {room} AND mo."UserId" = @me AND mo."LeftAt" IS NULL)""";

    // The caller is still an active seeker (role change or pause refuses with the socket still open).
    public static readonly string SeekerSql = $"""EXISTS (SELECT 1 FROM "Users" su WHERE su."Id" = @me AND {ActiveSeeker("su")})""";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // Target's MessagePolicy: Everyone; Following = he follows me; Buddies = I am in his My Buddies.
    public static bool PolicyAllows(string? policy, bool heFollowsMe, bool inHisBuddies) => policy switch
    {
        MessagePolicies.Following => heFollowsMe,
        MessagePolicies.Buddies => inHisBuddies,
        _ => true,
    };

    // ------------------------------------------------------------------ unread

    private static readonly string UnreadSql = $"""
        SELECT mu."UserId",
               COALESCE(sum(mu."UnreadCount") FILTER (WHERE mu."ListBucket" = '{ChatBuckets.People}'), 0)::int AS "People",
               COALESCE(sum(mu."UnreadCount") FILTER (WHERE mu."ListBucket" = '{ChatBuckets.Trips}'), 0)::int AS "Trips",
               COALESCE(sum(mu."UnreadCount") FILTER (WHERE mu."ListBucket" = '{ChatBuckets.Requests}'), 0)::int AS "Requests"
        FROM "ChatRoomMembers" mu
        WHERE mu."UserId" = ANY(@users) AND mu."UnreadCount" > 0 AND mu."LeftAt" IS NULL
        GROUP BY mu."UserId"
        """;

    public static async Task<Dictionary<Guid, ChatUnreadDto>> UnreadManyAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> users, CancellationToken ct = default)
    {
        if (users.Count == 0) return [];
        var rows = await db.Database.SqlQueryRaw<UnreadRow>(UnreadSql, Uuids("users", users.Distinct().ToArray())).ToListAsync(ct);
        return rows.ToDictionary(r => r.UserId, r => new ChatUnreadDto
        {
            People = r.People, Trips = r.Trips, Requests = r.Requests, Total = r.People + r.Trips + r.Requests,
        });
    }

    public static async Task<ChatUnreadDto> UnreadAsync(ApplicationDbContext db, Guid me, CancellationToken ct = default)
        => (await UnreadManyAsync(db, [me], ct)).GetValueOrDefault(me) ?? new ChatUnreadDto();

    // ------------------------------------------------------------------ rooms

    // Both face arrays read the same members in the same order (photo first), so photo i and name i are one person.
    private static readonly string FacesFrom = $"""
        FROM "ChatRoomMembers" x JOIN "Users" u2 ON u2."Id" = x."UserId"
                     WHERE x."RoomId" = r."Id" AND x."LeftAt" IS NULL AND x."UserId" <> @me AND r."Kind" = '{ChatKinds.Trip}'
                     ORDER BY (NULLIF(btrim(u2."ProfilePhotoUrl"), '') IS NULL), x."JoinedSeq", x."UserId" LIMIT {SocialLimits.RoomFacesMax}
        """;

    private static readonly string RoomSelect = $"""
        SELECT r."Id" AS "RoomId", r."Kind", r."Status", r."RequestedByUserId", r."LastSeq", r."LastPreview", r."LastMessageAt", r."TripId",
               r."BlockedAt", mm."LastReadSeq", mm."UnreadCount", mm."SortAt", mm."LeftAt",
               peer."Id" AS "PeerId", COALESCE(peer."Name", '') AS "PeerName", peer."Username" AS "PeerUsername",
               peer."ProfilePhotoUrl" AS "PeerPhoto", pp."HomeCityName" AS "PeerHomeCity", pp."Gender" AS "PeerGender", pm."LastReadSeq" AS "PeerLastReadSeq",
               (peer."Id" IS NOT NULL AND {AcceptedFollow("peer.\"Id\"", "@me")}) AS "PeerFollowsMe",
               (peer."Id" IS NOT NULL AND {AcceptedFollow("@me", "peer.\"Id\"")}) AS "IFollowPeer",
               t."Title" AS "TripTitle",
               (SELECT count(*) FROM "ChatRoomMembers" x WHERE x."RoomId" = r."Id" AND x."LeftAt" IS NULL)::int AS "MemberCount",
               t."CityKey" AS "TripCityKey",
               ARRAY(SELECT u2."ProfilePhotoUrl" {FacesFrom}) AS "MemberPhotos",
               ARRAY(SELECT COALESCE(u2."Name", '') {FacesFrom}) AS "MemberNames"
        FROM "ChatRoomMembers" mm
        JOIN "ChatRooms" r ON r."Id" = mm."RoomId"
        LEFT JOIN "ChatRoomMembers" pm ON r."Kind" = '{ChatKinds.Direct}' AND pm."RoomId" = r."Id" AND pm."UserId" <> @me
        LEFT JOIN "Users" peer ON peer."Id" = pm."UserId"
        LEFT JOIN "UserProfiles" pp ON pp."UserId" = peer."Id"
        LEFT JOIN "Trips" t ON t."Id" = r."TripId"
        WHERE mm."UserId" = @me
        """;

    private const string ListFilter = """ AND mm."ListBucket" = @bucket""";
    private const string ListKeyset = """ AND (mm."SortAt", r."Id") < (@at, @id)""";
    private const string ListOrder = """ ORDER BY mm."SortAt" DESC, r."Id" DESC LIMIT @take""";
    private const string ByIdFilter = """ AND r."Id" = @room""";
    private const string ByTripFilter = """ AND r."TripId" = @trip AND r."Kind" = 'T'""";

    public async Task<RoomsPageDto> RoomsAsync(Guid me, string bucket, (DateTime At, Guid Id)? cursor, int limit, CancellationToken ct = default)
    {
        var ps = new List<object> { Uuid("me", me), Text("bucket", bucket), Int("take", limit + 1) };
        if (cursor is { } c)
        {
            ps.Add(At("at", c.At));
            ps.Add(Uuid("id", c.Id));
        }
        var sql = RoomSelect + ListFilter + (cursor is null ? string.Empty : ListKeyset) + ListOrder;
        var rows = await db.Database.SqlQueryRaw<RoomRow>(sql, ps.ToArray()).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var limits = await seekerLimits.GetAsync(me, ct);
        return new RoomsPageDto
        {
            Items = page.Select(r => ToRoom(r, me, limits.IsPlus, limits.GroupChat)).ToList(),
            NextCursor = KeysetCursor.EncodeLast(page, rows.Count > limit, r => r.SortAt, r => r.RoomId),
            Unread = await UnreadAsync(db, me, ct),
        };
    }

    public async Task<ChatRoomDto?> RoomAsync(Guid me, Guid roomId, CancellationToken ct = default)
    {
        var r = (await db.Database.SqlQueryRaw<RoomRow>(RoomSelect + ByIdFilter, Uuid("me", me), Uuid("room", roomId)).ToListAsync(ct)).FirstOrDefault();
        if (r is null) return null;
        var limits = await seekerLimits.GetAsync(me, ct);
        return ToRoom(r, me, limits.IsPlus, limits.GroupChat);
    }

    public async Task<ChatRoomDto?> TripRoomAsync(Guid me, Guid tripId, CancellationToken ct = default)
    {
        var r = (await db.Database.SqlQueryRaw<RoomRow>(RoomSelect + ByTripFilter, Uuid("me", me), Uuid("trip", tripId)).ToListAsync(ct)).FirstOrDefault();
        if (r is null) return null;
        return ToRoom(r, me, false, (await seekerLimits.GetAsync(me, ct)).GroupChat);
    }

    internal static ChatRoomDto ToRoom(RoomRow r, Guid me, bool isPlus, bool groupChat = true)
    {
        var direct = r.Kind == ChatKinds.Direct;
        var iStarted = direct && r.RequestedByUserId == me;
        // A starter never learns of a decline: he keeps seeing his request.
        var status = r.Status == ChatRoomStatuses.Declined ? ChatRoomStatuses.Request : r.Status;
        var locked = LockOf(r.Kind, r.Status, iStarted, r.LastSeq, r.LeftAt != null, isPlus, groupChat);
        return new ChatRoomDto
        {
            RoomId = r.RoomId,
            Kind = r.Kind,
            Status = status,
            IStarted = iStarted,
            Peer = direct && r.PeerId is { } peerId ? Person(peerId, r.PeerName, r.PeerUsername, r.PeerPhoto, r.PeerHomeCity, r.PeerGender) : null,
            Trip = direct || r.TripId is null ? null : new RoomTripDto
            {
                TripId = r.TripId.Value, Title = r.TripTitle ?? string.Empty, MemberCount = r.MemberCount, CityKey = r.TripCityKey ?? 0,
                PhotoUrls = (r.MemberPhotos ?? []).Select(Face).OfType<string>().ToList(),
                MemberInitials = (r.MemberNames ?? []).Select(SocialRules.Initial).ToList(),
            },
            PeerFollowsMe = r.PeerFollowsMe,
            IFollowPeer = r.IFollowPeer,
            LastSeq = r.LastSeq,
            LastReadSeq = r.LastReadSeq,
            PeerLastReadSeq = direct && r.Status == ChatRoomStatuses.Active ? r.PeerLastReadSeq : null,
            Unread = r.LeftAt != null ? 0 : r.UnreadCount,
            LastMessageAt = r.LastMessageAt is { } at ? Utc(at) : null,
            Preview = r.LastPreview,
            CanSend = locked == ChatLocks.None,
            Lock = locked,
        };
    }

    // awaitingReply: a FREE starter of a Request room after his first message (a Plus starter is not locked); left: trip room left,
    // or a Direct room the caller declined (contract 2.6: the decliner cannot message for 30 days; the starter never learns of it).
    // plus (owner 2026-11-27): a trip room member without the SeekerGroupChat switch reads only (Plus prompt in place of the composer).
    public static string LockOf(string kind, string status, bool iStarted, long lastSeq, bool left, bool isPlus, bool groupChat = true)
    {
        if (left) return ChatLocks.Left;
        if (kind == ChatKinds.Trip && !groupChat) return ChatLocks.Plus;
        if (kind == ChatKinds.Direct && status == ChatRoomStatuses.Declined && !iStarted) return ChatLocks.Left;
        if (kind == ChatKinds.Direct && iStarted && lastSeq >= 1
            && (status == ChatRoomStatuses.Declined || (status == ChatRoomStatuses.Request && !isPlus)))
            return ChatLocks.AwaitingReply;
        return ChatLocks.None;
    }

    // ------------------------------------------------------------------ start (POST /chat/rooms) and preflight

    private static readonly string PairSql = $"""
        SELECT t."Id" AS "TargetId", COALESCE(t."Name", '') AS "TargetName", t."Username" AS "TargetUsername",
               t."ProfilePhotoUrl" AS "TargetPhoto", tp."HomeCityName" AS "TargetHomeCity", tp."Gender" AS "TargetGender", tp."MessagePolicy", tp."NotifyMessages",
               {AcceptedFollow("@him", "@me")} AS "HeFollowsMe",
               {AcceptedFollow("@me", "@him")} AS "IFollowHim",
               {InBuddies("@him", "@me")} AS "InHisBuddies",
               EXISTS (SELECT 1 FROM "Trips" tr WHERE tr."Status" <> '{TripStatuses.Completed}'
                       AND (tr."OwnerUserId" = @me OR EXISTS (SELECT 1 FROM "TripMembers" m1 WHERE m1."TripId" = tr."Id" AND m1."UserId" = @me AND m1."Status" = '{TripMemberStatuses.Joined}'))
                       AND (tr."OwnerUserId" = @him OR EXISTS (SELECT 1 FROM "TripMembers" m2 WHERE m2."TripId" = tr."Id" AND m2."UserId" = @him AND m2."Status" = '{TripMemberStatuses.Joined}'))) AS "SharedTrip",
               {JoinBlocks("@me", "@him")} AS "JoinBlocks",
               r."Id" AS "RoomId", r."Status" AS "RoomStatus", r."RequestedByUserId" AS "RoomStarter",
               (SELECT count(*) FROM "ChatRooms" c WHERE c."RequestedByUserId" = @me AND c."ChargedAt" >= @month)::int AS "Charged"
        FROM "Users" t
        JOIN "UserProfiles" tp ON tp."UserId" = t."Id"
        LEFT JOIN "ChatRooms" r ON r."Kind" = '{ChatKinds.Direct}' AND r."PairLowId" = LEAST(@me, @him) AND r."PairHighId" = GREATEST(@me, @him)
          AND {MemberOf("r.\"Id\"")}
        WHERE t."Id" = @him AND {ActiveSeeker("t")} AND NOT {BlockedEitherWay("@me", "@him")}
        """;

    private async Task<PairRow?> PairAsync(Guid me, Guid him, CancellationToken ct)
        => me == him ? null : (await db.Database.SqlQueryRaw<PairRow>(PairSql, Uuid("me", me), Uuid("him", him),
            At("month", SocialMonth.StartUtc(Now))).ToListAsync(ct)).FirstOrDefault();

    private static bool IsFree(PairRow p) => p.InHisBuddies || (p.HeFollowsMe && p.IFollowHim) || p.SharedTrip;

    private static bool CanMessage(PairRow p) => PolicyAllows(p.MessagePolicy, p.HeFollowsMe, p.InHisBuddies) && !p.JoinBlocks;

    // The decliner of a Direct request (contract 2.6): 403 CannotMessage, no room shown. The starter keeps his request view.
    private static bool IDeclined(PairRow p, Guid me) => p.RoomId != null && p.RoomStatus == ChatRoomStatuses.Declined && p.RoomStarter != me;

    public async Task<BuddyResult<ChatPreflightDto>> WithUserAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var p = await PairAsync(me, him, ct);
        if (p is null) return BuddyResult<ChatPreflightDto>.Fail(SocialErrorTypes.PersonNotFound);
        var limits = await seekerLimits.GetAsync(me, ct);
        var declined = IDeclined(p, me);
        var room = !declined && p.RoomId is { } roomId ? await RoomAsync(me, roomId, ct) : null;
        var free = IsFree(p);
        var canMessage = !declined && (room != null || CanMessage(p));
        var locked = room?.Lock ?? (!free && p.Charged >= limits.NewChatsPerMonth ? ChatLocks.Limit : ChatLocks.None);
        return BuddyResult<ChatPreflightDto>.Success(new ChatPreflightDto
        {
            Room = room,
            Peer = Person(p.TargetId, p.TargetName, p.TargetUsername, p.TargetPhoto, p.TargetHomeCity, p.TargetGender),
            CanMessage = canMessage,
            Lock = locked,
            NewChats = new LimitUseDto { Limit = limits.NewChatsPerMonth, Used = p.Charged },
            IsFree = free,
        });
    }

    private static readonly string InsertRoomSql = $"""
        INSERT INTO "ChatRooms" ("Id", "Kind", "PairLowId", "PairHighId", "Status", "RequestedByUserId", "ChargedAt", "LastSeq", "CreatedAt")
        VALUES (gen_random_uuid(), '{ChatKinds.Direct}', LEAST(@me, @him), GREATEST(@me, @him), @status, @me, @charged, 0, now())
        ON CONFLICT ("PairLowId", "PairHighId") WHERE "Kind" = '{ChatKinds.Direct}' DO NOTHING
        RETURNING "Id" AS "Value"
        """;

    private static readonly string InsertPairMembersSql = $"""
        INSERT INTO "ChatRoomMembers" ("RoomId", "UserId", "LastReadSeq", "UnreadCount", "ListBucket", "ClearedUpToSeq", "JoinedSeq", "SortAt")
        SELECT @room, x.uid, 0, 0, x.bucket, 0, 0, now()
        FROM (VALUES (@me, '{ChatBuckets.People}'), (@him, @hisBucket)) AS x(uid, bucket)
        WHERE EXISTS (SELECT 1 FROM "ChatRooms" r WHERE r."Id" = @room AND r."RequestedByUserId" = @me)
        ON CONFLICT DO NOTHING
        """;

    public async Task<BuddyResult<StartedChatDto>> StartAsync(Guid me, Guid him, Guid clientMessageId, string text, CancellationToken ct = default)
    {
        var clean = CleanText(text);
        if (clean is null) return BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.TextTooLong);
        if (!await GuidelinesAcceptedAsync(db, me, ct)) return BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.GuidelinesNotAccepted);
        var p = await PairAsync(me, him, ct);
        if (p is null) return BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.PersonNotFound);
        if (IDeclined(p, me)) return BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.CannotMessage);
        if (p.RoomId is { } existing) return await SendAsStartAsync(me, existing, clientMessageId, clean, ct);
        if (!CanMessage(p)) return BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.CannotMessage);

        var free = IsFree(p);
        var limits = await seekerLimits.GetAsync(me, ct);
        var myName = await MyNameAsync(me, ct);
        var outcome = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", c);
            await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            if (!free)
            {
                var used = (await db.Database.SqlQueryRaw<int>(
                    """SELECT count(*)::int AS "Value" FROM "ChatRooms" c WHERE c."RequestedByUserId" = @me AND c."ChargedAt" >= @month""",
                    Uuid("me", me), At("month", SocialMonth.StartUtc(Now))).ToListAsync(c)).First();
                if (used >= limits.NewChatsPerMonth)
                    return (Room: (Guid?)null, Error: BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.NewChatLimit,
                        SocialProfileService.CapData(limits.NewChatsPerMonth, used, limits.CanUpgrade, Now)));
            }
            var status = free ? ChatRoomStatuses.Active : ChatRoomStatuses.Request;
            var roomId = (await db.Database.SqlQueryRaw<Guid>(InsertRoomSql, Uuid("me", me), Uuid("him", him), Text("status", status),
                new NpgsqlParameter("charged", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = free ? DBNull.Value : (object)Utc(Now) }).ToListAsync(c)).FirstOrDefault();
            if (roomId == Guid.Empty) return (Room: (Guid?)null, Error: (BuddyResult<ChatMessageDto>?)null);
            await db.Database.ExecuteSqlRawAsync(InsertPairMembersSql,
                [Uuid("room", roomId), Uuid("me", me), Uuid("him", him), Text("hisBucket", free ? ChatBuckets.People : ChatBuckets.Requests)], c);
            var sent = await SendInTxAsync(me, roomId, clientMessageId, clean, myName, limits.IsPlus, limits.GroupChat, c);
            if (!sent.Ok) return (Room: (Guid?)roomId, Error: sent);
            await tx.CommitAsync(c);
            return (Room: (Guid?)roomId, Error: sent);
        }, ct, logger);

        // Lost the pair race: the room exists now, so this is a send.
        if (outcome.Room is null && outcome.Error is null)
        {
            var again = await PairAsync(me, him, ct);
            return again?.RoomId is { } raced
                ? await SendAsStartAsync(me, raced, clientMessageId, clean, ct)
                : BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.PersonNotFound);
        }
        if (outcome.Error is { Ok: false } failed) return BuddyResult<StartedChatDto>.Fail(failed.ErrorType!, failed.ErrorData);
        await afterCommit.FlushAsync(ct);
        var room = await RoomAsync(me, outcome.Room!.Value, ct);
        return BuddyResult<StartedChatDto>.Success(new StartedChatDto { Room = room!, Message = outcome.Error!.Value! });
    }

    private async Task<BuddyResult<StartedChatDto>> SendAsStartAsync(Guid me, Guid roomId, Guid clientMessageId, string text, CancellationToken ct)
    {
        var sent = await SendCoreAsync(me, roomId, clientMessageId, text, ct);
        if (!sent.Ok) return BuddyResult<StartedChatDto>.Fail(sent.ErrorType!, sent.ErrorData);
        var room = await RoomAsync(me, roomId, ct);
        return room is null
            ? BuddyResult<StartedChatDto>.Fail(SocialErrorTypes.RoomNotFound)
            : BuddyResult<StartedChatDto>.Success(new StartedChatDto { Room = room, Message = sent.Value!, IntoExistingRoom = true });
    }

    // ------------------------------------------------------------------ send / read

    private static string? CleanText(string? text)
    {
        var t = ContactMask.StripInvisible(text ?? string.Empty).Trim();
        return t.Length is 0 or > SocialLimits.ChatTextMax ? null : t;
    }

    private async Task<string> MyNameAsync(Guid me, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

    private static readonly string ReplaySql = $"""
        SELECT m."Seq", m."SenderId", su2."Name" AS "SenderName", m."Body", m."Masked", m."SystemKind", m."CreatedAt", m."ExpiresAt", m."ClientMessageId"
        FROM "ChatMessages" m LEFT JOIN "Users" su2 ON su2."Id" = m."SenderId"
        WHERE m."RoomId" = @room AND m."SenderId" = @me AND m."ClientMessageId" = @client AND {MemberOf("m.\"RoomId\"")}
        """;

    private static readonly string BumpSql = $"""
        WITH old AS (SELECT r0."Status" FROM "ChatRooms" r0 WHERE r0."Id" = @room)
        UPDATE "ChatRooms" r SET "LastSeq" = r."LastSeq" + 1, "LastMessageAt" = now(), "LastPreview" = @preview,
               "Status" = CASE WHEN r."Status" = '{ChatRoomStatuses.Request}' AND r."RequestedByUserId" <> @me THEN '{ChatRoomStatuses.Active}' ELSE r."Status" END
        WHERE r."Id" = @room AND {MemberOf("r.\"Id\"")} AND {SeekerSql}
          AND NOT (r."Status" = '{ChatRoomStatuses.Request}' AND r."RequestedByUserId" = @me AND r."LastSeq" >= 1 AND NOT @plus)
          AND r."Status" <> '{ChatRoomStatuses.Declined}'
        RETURNING r."LastSeq" AS "Seq", r."Status", r."Kind", r."BlockedAt", (SELECT old."Status" FROM old) AS "OldStatus"
        """;

    private static readonly string StateSql = $"""
        SELECT r."Status", r."RequestedByUserId", r."LastSeq", r."Kind" FROM "ChatRooms" r
        WHERE r."Id" = @room AND {MemberOf("r.\"Id\"")}
        """;

    private static readonly string InsertMessageSql = $"""
        INSERT INTO "ChatMessages" ("RoomId", "Seq", "SenderId", "ClientMessageId", "Body", "Masked", "CreatedAt", "ExpiresAt")
        SELECT @room, @seq, @me, @client, @body, @masked, now(), now() + make_interval(secs => @life)
        WHERE {MemberOf("@room")}
        ON CONFLICT ("RoomId", "SenderId", "ClientMessageId") WHERE "ClientMessageId" IS NOT NULL DO NOTHING
        RETURNING "CreatedAt", "ExpiresAt"
        """;

    // A reply (or a decline) brings the waiting first message back to the normal 24 hour life.
    private static readonly string ShortenSql = $"""
        UPDATE "ChatMessages" m SET "ExpiresAt" = now() + {Interval(SocialLimits.MessageLife)}
        WHERE m."RoomId" = @room AND m."ExpiresAt" > now() + {Interval(SocialLimits.MessageLife)} AND {MemberOf("m.\"RoomId\"")}
        """;

    private static readonly string FanOutSql = $"""
        UPDATE "ChatRoomMembers" m SET "SortAt" = now(),
               "UnreadCount" = m."UnreadCount" + CASE WHEN m."UserId" <> @me THEN 1 ELSE 0 END,
               "LastReadSeq" = CASE WHEN m."UserId" = @me THEN @seq ELSE m."LastReadSeq" END,
               "ListBucket" = CASE WHEN m."ListBucket" = '{ChatBuckets.Hidden}' OR (m."ListBucket" = '{ChatBuckets.Requests}' AND @activated)
                                   THEN @bucket ELSE m."ListBucket" END
        WHERE m."RoomId" = @room AND m."LeftAt" IS NULL AND {MemberOf("@room")}
        RETURNING m."UserId" AS "Value"
        """;

    private const string NotifySql = """
        SELECT p."UserId" AS "Value" FROM "UserProfiles" p WHERE p."UserId" = ANY(@users) AND p."NotifyMessages"
        """;

    // Spec 25: every send (first message, a receiver's reply, a trip group line) needs the accepted guidelines, checked before
    // any limit is charged and before anything is stored.
    public async Task<BuddyResult<ChatMessageDto>> SendAsync(Guid me, Guid roomId, Guid clientMessageId, string text, CancellationToken ct = default)
    {
        if (CleanText(text) is null) return BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.TextTooLong);
        if (!await GuidelinesAcceptedAsync(db, me, ct)) return BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.GuidelinesNotAccepted);
        return await SendCoreAsync(me, roomId, clientMessageId, text, ct);
    }

    private async Task<BuddyResult<ChatMessageDto>> SendCoreAsync(Guid me, Guid roomId, Guid clientMessageId, string text, CancellationToken ct)
    {
        var clean = CleanText(text);
        if (clean is null) return BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.TextTooLong);
        var replay = await ReplayAsync(me, roomId, clientMessageId, ct);
        if (replay != null) return BuddyResult<ChatMessageDto>.Success(replay);
        var limits = await seekerLimits.GetAsync(me, ct);
        var isPlus = limits.IsPlus;
        var myName = await MyNameAsync(me, ct);
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", c);
            var sent = await SendInTxAsync(me, roomId, clientMessageId, clean, myName, isPlus, limits.GroupChat, c);
            if (sent.Ok) await tx.CommitAsync(c);
            return sent;
        }, ct, logger);
        if (result.Ok)
        {
            await afterCommit.FlushAsync(ct);
            return result;
        }
        // A parallel send with the same client id won: answer with the stored message.
        if (result.ErrorType == ReplayMarker)
            return await ReplayAsync(me, roomId, clientMessageId, ct) is { } stored
                ? BuddyResult<ChatMessageDto>.Success(stored)
                : BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.RoomNotFound);
        return result;
    }

    private const string ReplayMarker = "__replay";

    private async Task<ChatMessageDto?> ReplayAsync(Guid me, Guid roomId, Guid clientMessageId, CancellationToken ct)
    {
        var r = (await db.Database.SqlQueryRaw<MessageRow>(ReplaySql, Uuid("me", me), Uuid("room", roomId), Uuid("client", clientMessageId))
            .ToListAsync(ct)).FirstOrDefault();
        return r is null ? null : ToMessage(r);
    }

    // Inside the caller's transaction. Hub events, unread counts and the wake go to SocialAfterCommit.
    private async Task<BuddyResult<ChatMessageDto>> SendInTxAsync(
        Guid me, Guid roomId, Guid clientMessageId, string clean, string myName, bool isPlus, bool groupChat, CancellationToken c)
    {
        var (body, masked) = ContactMask.Apply(clean);
        if (body.Length > SocialLimits.ChatTextMax) body = body[..SocialLimits.ChatTextMax];
        var kind = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me), Uuid("room", roomId)).ToListAsync(c)).FirstOrDefault();
        if (kind is null) return BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.RoomNotFound);
        // Owner 2026-11-27: a trip group line needs the SeekerGroupChat switch (Plus); refused before anything is stored.
        if (kind.Kind == ChatKinds.Trip && !groupChat) return BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.PlusRequired);
        var preview = SocialRules.Preview(body, kind.Kind == ChatKinds.Trip ? SocialRules.FirstName(myName) : null);

        var bumped = (await db.Database.SqlQueryRaw<BumpRow>(BumpSql, Uuid("me", me), Uuid("room", roomId), Text("preview", preview), Bool("plus", isPlus))
            .ToListAsync(c)).FirstOrDefault();
        if (bumped is null)
        {
            // One re-read tells the cases apart: not a member (or no longer a seeker) vs a starter waiting for a reply.
            var state = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me), Uuid("room", roomId)).ToListAsync(c)).FirstOrDefault();
            return state is not null && state.RequestedByUserId == me && state.Status is ChatRoomStatuses.Request or ChatRoomStatuses.Declined
                ? BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.AwaitingReply)
                : BuddyResult<ChatMessageDto>.Fail(SocialErrorTypes.RoomNotFound);
        }

        var activated = bumped.OldStatus == ChatRoomStatuses.Request && bumped.Status == ChatRoomStatuses.Active;
        var life = bumped.Status == ChatRoomStatuses.Request ? SocialLimits.RequestFirstMessageLife : SocialLimits.MessageLife;
        var inserted = (await db.Database.SqlQueryRaw<InsertedRow>(InsertMessageSql,
            Uuid("room", roomId), Long("seq", bumped.Seq), Uuid("me", me), Uuid("client", clientMessageId), Text("body", body),
            Bool("masked", masked), Double("life", life.TotalSeconds)).ToListAsync(c)).FirstOrDefault();
        if (inserted is null) return BuddyResult<ChatMessageDto>.Fail(ReplayMarker);
        if (activated) await db.Database.ExecuteSqlRawAsync(ShortenSql, [Uuid("me", me), Uuid("room", roomId)], c);

        var message = new ChatMessageDto
        {
            Seq = bumped.Seq, SenderId = me, SenderName = SocialRules.BubbleName(myName), Text = body, Masked = masked,
            CreatedAt = Utc(inserted.CreatedAt), ExpiresAt = Utc(inserted.ExpiresAt), ClientMessageId = clientMessageId,
        };
        // A send into a blocked room is stored and acked only: no member row, no event, no push.
        if (bumped.BlockedAt != null) return BuddyResult<ChatMessageDto>.Success(message);

        var members = await db.Database.SqlQueryRaw<Guid>(FanOutSql,
            Uuid("me", me), Uuid("room", roomId), Long("seq", bumped.Seq), Bool("activated", activated),
            Text("bucket", bumped.Kind == ChatKinds.Trip ? ChatBuckets.Trips : ChatBuckets.People)).ToListAsync(c);
        var receivers = members.Where(u => u != me).ToList();
        if (receivers.Count > 0)
        {
            var notify = await db.Database.SqlQueryRaw<Guid>(NotifySql, Uuids("users", receivers)).ToListAsync(c);
            var now = Now;
            var pushes = notify.Select(u => bumped.Kind == ChatKinds.Trip
                ? SocialPushCopy.TripMessage(u, roomId, now)
                : SocialPushCopy.DirectMessage(u, roomId, myName, now)).ToList();
            if (pushes.Count > 0)
            {
                await notices.PushAsync(pushes, c);
                afterCommit.Wake();
            }
        }
        var roomStatus = bumped.Status;
        afterCommit.AddRange(members.Select(u => new SocialHubEvent(u, SocialHubEventNames.DmMessage,
            new { roomId, kind = bumped.Kind, roomStatus, message })));
        if (activated)
            afterCommit.AddRange(members.Select(u => new SocialHubEvent(u, SocialHubEventNames.DmRoomChanged,
                new { roomId, status = roomStatus, @lock = ChatLocks.None })));
        afterCommit.UnreadFor(receivers);
        return BuddyResult<ChatMessageDto>.Success(message);
    }

    private static readonly string ReadSql = $"""
        UPDATE "ChatRoomMembers" m
        SET "LastReadSeq" = GREATEST(m."LastReadSeq", LEAST(@seq, (SELECT r."LastSeq" FROM "ChatRooms" r WHERE r."Id" = m."RoomId"))),
            "UnreadCount" = (SELECT count(*)::int FROM "ChatMessages" x
                             WHERE x."RoomId" = m."RoomId" AND x."ExpiresAt" > now() AND x."Seq" > m."JoinedSeq"
                               AND x."Seq" > GREATEST(m."LastReadSeq", LEAST(@seq, (SELECT r."LastSeq" FROM "ChatRooms" r WHERE r."Id" = m."RoomId")))
                               AND (x."SenderId" IS NULL OR x."SenderId" <> @me))
        WHERE m."RoomId" = @room AND m."UserId" = @me AND m."LeftAt" IS NULL AND {SeekerSql}
        RETURNING m."LastReadSeq" AS "Seq",
                  (SELECT r."Kind" FROM "ChatRooms" r WHERE r."Id" = m."RoomId") AS "Kind",
                  (SELECT r."Status" FROM "ChatRooms" r WHERE r."Id" = m."RoomId") AS "Status",
                  (SELECT x."UserId" FROM "ChatRoomMembers" x WHERE x."RoomId" = m."RoomId" AND x."UserId" <> @me AND x."LeftAt" IS NULL LIMIT 1) AS "PeerId",
                  (SELECT r."BlockedAt" FROM "ChatRooms" r WHERE r."Id" = m."RoomId") AS "BlockedAt"
        """;

    public async Task<BuddyResult<ChatUnreadDto>> ReadAsync(Guid me, Guid roomId, long seq, CancellationToken ct = default)
    {
        afterCommit.Clear();
        var r = (await db.Database.SqlQueryRaw<ReadRow>(ReadSql, Uuid("me", me), Uuid("room", roomId), Long("seq", Math.Max(0, seq))).ToListAsync(ct))
            .FirstOrDefault();
        if (r is null) return BuddyResult<ChatUnreadDto>.Fail(SocialErrorTypes.RoomNotFound);
        // A Request room never tells the starter it was read.
        if (r.Kind == ChatKinds.Direct && r.Status == ChatRoomStatuses.Active && r.PeerId is { } peer && r.BlockedAt is null)
            afterCommit.Add(new SocialHubEvent(peer, SocialHubEventNames.DmRead, new { roomId, userId = me, seq = r.Seq }));
        afterCommit.UnreadFor([me]);
        await afterCommit.FlushAsync(ct);
        return BuddyResult<ChatUnreadDto>.Success(await UnreadAsync(db, me, ct));
    }

    // ------------------------------------------------------------------ messages page

    private static readonly string MessagesSql = $"""
        SELECT m."Seq", m."SenderId", su2."Name" AS "SenderName", m."Body", m."Masked", m."SystemKind", m."CreatedAt", m."ExpiresAt", m."ClientMessageId"
        FROM "ChatMessages" m
        JOIN "ChatRoomMembers" mm ON mm."RoomId" = m."RoomId" AND mm."UserId" = @me AND mm."LeftAt" IS NULL
        LEFT JOIN "Users" su2 ON su2."Id" = m."SenderId"
        WHERE m."RoomId" = @room AND m."ExpiresAt" > now() AND m."Seq" > mm."ClearedUpToSeq" AND m."Seq" > mm."JoinedSeq"
          AND m."Seq" > @after AND m."Seq" < @before AND {MemberOf("m.\"RoomId\"")}
        ORDER BY m."Seq" {"{0}"}
        LIMIT @take
        """;

    private static readonly string RoomHeadSql = $"""
        SELECT r."LastSeq" AS "Seq", r."Kind", r."Status",
               (SELECT x."LastReadSeq" FROM "ChatRoomMembers" x WHERE x."RoomId" = r."Id" AND x."UserId" <> @me LIMIT 1) AS "PeerLastReadSeq"
        FROM "ChatRooms" r WHERE r."Id" = @room AND {MemberOf("r.\"Id\"")}
        """;

    public async Task<BuddyResult<ChatMessagesPageDto>> MessagesAsync(Guid me, Guid roomId, long? beforeSeq, long? afterSeq, int? limit, CancellationToken ct = default)
    {
        var head = (await db.Database.SqlQueryRaw<HeadRow>(RoomHeadSql, Uuid("me", me), Uuid("room", roomId)).ToListAsync(ct)).FirstOrDefault();
        if (head is null) return BuddyResult<ChatMessagesPageDto>.Fail(SocialErrorTypes.RoomNotFound);
        var take = limit is null ? SocialLimits.MessagesPageDefault : Math.Clamp(limit.Value, 1, SocialLimits.MessagesPageMax);
        // afterSeq reads forward (gap fill); otherwise the newest page before beforeSeq.
        var forward = afterSeq != null && beforeSeq == null;
        var sql = string.Format(MessagesSql, forward ? "ASC" : "DESC");
        var rows = await db.Database.SqlQueryRaw<MessageRow>(sql, Uuid("me", me), Uuid("room", roomId),
            Long("after", afterSeq ?? 0), Long("before", beforeSeq ?? long.MaxValue), Int("take", take)).ToListAsync(ct);
        var items = rows.Select(ToMessage).OrderBy(m => m.Seq).ToList();
        return BuddyResult<ChatMessagesPageDto>.Success(new ChatMessagesPageDto
        {
            Items = items,
            LastSeq = head.Seq,
            PeerLastReadSeq = head.Kind == ChatKinds.Direct && head.Status == ChatRoomStatuses.Active ? head.PeerLastReadSeq : null,
        });
    }

    // ------------------------------------------------------------------ delete

    private static readonly string DeclineSql = $"""
        WITH r AS (
          UPDATE "ChatRooms" r SET "Status" = '{ChatRoomStatuses.Declined}', "DeclinedAt" = now()
          WHERE r."Id" = @room AND r."Kind" = '{ChatKinds.Direct}' AND r."Status" = '{ChatRoomStatuses.Request}' AND r."RequestedByUserId" <> @me
            AND {MemberOf("r.\"Id\"")}
          RETURNING r."Id")
        UPDATE "ChatRoomMembers" m SET "ListBucket" = '{ChatBuckets.Hidden}', "UnreadCount" = 0
        WHERE m."RoomId" IN (SELECT "Id" FROM r) AND m."UserId" = @me
        """;

    private static readonly string ClearSql = $"""
        UPDATE "ChatRoomMembers" m
        SET "ClearedUpToSeq" = (SELECT r."LastSeq" FROM "ChatRooms" r WHERE r."Id" = m."RoomId"), "ListBucket" = '{ChatBuckets.Hidden}', "UnreadCount" = 0,
            "LastReadSeq" = (SELECT r."LastSeq" FROM "ChatRooms" r WHERE r."Id" = m."RoomId")
        WHERE m."RoomId" = @room AND m."UserId" = @me AND m."LeftAt" IS NULL
          AND EXISTS (SELECT 1 FROM "ChatRooms" r WHERE r."Id" = m."RoomId" AND r."Kind" = '{ChatKinds.Direct}')
        """;

    public async Task<BuddyResult<bool>> DeleteRoomAsync(Guid me, Guid roomId, CancellationToken ct = default)
    {
        var state = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me), Uuid("room", roomId)).ToListAsync(ct)).FirstOrDefault();
        if (state is null) return BuddyResult<bool>.Fail(SocialErrorTypes.RoomNotFound);
        if (state.Kind == ChatKinds.Trip) return BuddyResult<bool>.Fail(SocialErrorTypes.NotAllowed);
        await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (state.Status == ChatRoomStatuses.Request && state.RequestedByUserId != me)
            {
                await db.Database.ExecuteSqlRawAsync(ShortenSql, [Uuid("me", me), Uuid("room", roomId)], c);
                await db.Database.ExecuteSqlRawAsync(DeclineSql, [Uuid("me", me), Uuid("room", roomId)], c);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(ClearSql, [Uuid("me", me), Uuid("room", roomId)], c);
            }
            await tx.CommitAsync(c);
        }, ct, logger);
        afterCommit.Clear();
        afterCommit.UnreadFor([me]);
        await afterCommit.FlushAsync(ct);
        return BuddyResult<bool>.Success(true);
    }

    // ------------------------------------------------------------------ mapping

    internal static ChatMessageDto ToMessage(MessageRow r) => new()
    {
        Seq = r.Seq,
        SenderId = r.SenderId,
        SenderName = r.SenderId is null ? null : SocialRules.BubbleName(r.SenderName),
        Text = r.Body,
        Masked = r.Masked,
        System = r.SystemKind,
        CreatedAt = Utc(r.CreatedAt),
        ExpiresAt = Utc(r.ExpiresAt),
        ClientMessageId = r.ClientMessageId,
    };

    internal sealed class RoomRow
    {
        public Guid RoomId { get; set; }
        public string Kind { get; set; } = ChatKinds.Direct;
        public string Status { get; set; } = ChatRoomStatuses.Active;
        public Guid? RequestedByUserId { get; set; }
        public long LastSeq { get; set; }
        public string? LastPreview { get; set; }
        public DateTime? LastMessageAt { get; set; }
        public Guid? TripId { get; set; }
        public DateTime? BlockedAt { get; set; }
        public long LastReadSeq { get; set; }
        public int UnreadCount { get; set; }
        public DateTime SortAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public Guid? PeerId { get; set; }
        public string PeerName { get; set; } = string.Empty;
        public string? PeerUsername { get; set; }
        public string? PeerPhoto { get; set; }
        public string? PeerHomeCity { get; set; }
        public string? PeerGender { get; set; }
        public long? PeerLastReadSeq { get; set; }
        public bool PeerFollowsMe { get; set; }
        public bool IFollowPeer { get; set; }
        public string? TripTitle { get; set; }
        public int MemberCount { get; set; }
        public string?[]? MemberPhotos { get; set; }
        public string?[]? MemberNames { get; set; }
        public long? TripCityKey { get; set; }
    }

    internal sealed class MessageRow
    {
        public long Seq { get; set; }
        public Guid? SenderId { get; set; }
        public string? SenderName { get; set; }
        public string Body { get; set; } = string.Empty;
        public bool Masked { get; set; }
        public string? SystemKind { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public Guid? ClientMessageId { get; set; }
    }

    private sealed class PairRow
    {
        public Guid TargetId { get; set; }
        public string TargetName { get; set; } = string.Empty;
        public string? TargetUsername { get; set; }
        public string? TargetPhoto { get; set; }
        public string? TargetHomeCity { get; set; }
        public string? TargetGender { get; set; }
        public string MessagePolicy { get; set; } = MessagePolicies.Everyone;
        public bool NotifyMessages { get; set; }
        public bool HeFollowsMe { get; set; }
        public bool IFollowHim { get; set; }
        public bool InHisBuddies { get; set; }
        public bool SharedTrip { get; set; }
        public bool JoinBlocks { get; set; }
        public Guid? RoomId { get; set; }
        public string? RoomStatus { get; set; }
        public Guid? RoomStarter { get; set; }
        public int Charged { get; set; }
    }

    private sealed class StateRow
    {
        public string Status { get; set; } = string.Empty;
        public Guid? RequestedByUserId { get; set; }
        public long LastSeq { get; set; }
        public string Kind { get; set; } = string.Empty;
    }

    private sealed class BumpRow
    {
        public long Seq { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public DateTime? BlockedAt { get; set; }
        public string? OldStatus { get; set; }
    }

    private sealed class InsertedRow
    {
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class ReadRow
    {
        public long Seq { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid? PeerId { get; set; }
        public DateTime? BlockedAt { get; set; }
    }

    private sealed class HeadRow
    {
        public long Seq { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long? PeerLastReadSeq { get; set; }
    }

    private sealed class UnreadRow
    {
        public Guid UserId { get; set; }
        public int People { get; set; }
        public int Trips { get; set; }
        public int Requests { get; set; }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

public sealed record ProfileBuild(UserProfile? Profile, string? ErrorType, object? ErrorData);

// Contract 2.2: social profile, onboarding, privacy, traveller profile, limits and the admin identity correction.
public sealed class SocialProfileService(
    ApplicationDbContext db, ISeekerLimits seekerLimits, PublicTripReader trips, SocialAfterCommit afterCommit,
    TimeProvider clock, ILogger<SocialProfileService> logger, SeekerPlanReconcile? seekerPlans = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // The onboarding body as a UserProfile row (new and existing accounts share it). Under 18 = Under18, nothing stored.
    public static ProfileBuild Build(Guid userId, SocialOnboardingRequest r, DateOnly todayIst)
    {
        if (r.Gender is null || !SocialGenders.All.Contains(r.Gender)) return Invalid("gender");
        if (r.BirthMonth is not { } month || r.BirthYear is not { } year || !SocialRules.IsValidBirth(year, month)) return Invalid("birth");
        if (!SocialRules.IsAdult(year, month, todayIst)) return new ProfileBuild(null, SocialErrorTypes.Under18, null);
        var interests = SocialRules.CatalogList(r.Interests, SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax);
        if (interests is null) return Invalid("interests");
        var skills = SocialRules.CatalogList(r.Skills, SocialCatalog.Skills, SocialLimits.SkillsMin, SocialLimits.SkillsMax);
        if (skills is null) return Invalid("skills");
        var places = SocialRules.Places(r.Places);
        if (places is null) return Invalid("places");
        if (places.Any(SocialRules.PlaceHasContact)) return NotAllowed("places");
        var about = CleanAbout(r.About);
        if (about.Error is { } aboutError) return aboutError;
        var city = CleanHome(r.HomeCityName, SocialLimits.HomeCityMax);
        var state = CleanHome(r.HomeStateName, SocialLimits.HomeStateMax);
        if (city.Check == HomeCheck.Bad) return Invalid("homeCity");
        if (state.Check == HomeCheck.Bad) return Invalid("homeState");
        if (city.Check == HomeCheck.NotAllowed) return NotAllowed("homeCity");
        if (state.Check == HomeCheck.NotAllowed) return NotAllowed("homeState");
        return new ProfileBuild(new UserProfile
        {
            UserId = userId, Gender = r.Gender, BirthMonth = (short)month, BirthYear = (short)year, About = about.Text!,
            Interests = interests, Skills = skills, Places = places, HomeCityName = city.Value, HomeStateName = state.Value,
            IsPrivate = false, MessagePolicy = MessagePolicies.Everyone, NotifyMessages = true, NotifyFollows = true, NotifyFollowedTrips = true,
        }, null, null);
    }

    // All seven required onboarding fields present (the optional block of complete-onboarding for new accounts).
    public static bool HasOnboarding(SocialOnboardingRequest r)
        => r.Gender != null && r.BirthMonth != null && r.BirthYear != null && r.Interests != null && r.Skills != null && r.Places != null && r.About != null;

    private static ProfileBuild Invalid(string field) => new(null, SocialErrorTypes.InvalidProfile, new { field });

    private static ProfileBuild NotAllowed(string field) => new(null, SocialErrorTypes.TextNotAllowed, new { field });

    private static (string? Text, ProfileBuild? Error) CleanAbout(string? about)
    {
        var text = ContactMask.StripInvisible(about ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > SocialLimits.AboutMax) return (null, Invalid("about"));
        var (masked, hit) = ContactMask.Apply(text);
        if (hit && ContactMask.IsOnlyMarks(masked)) return (null, NotAllowed("about"));
        if (masked.Length > SocialLimits.AboutMax) masked = masked[..SocialLimits.AboutMax];
        return (masked, null);
    }

    private enum HomeCheck { Ok, Bad, NotAllowed }

    // Home city / state (contract line 268): invisible characters stripped; too long = Bad (InvalidProfile); any contact
    // (phone digits, email, link, @handle) = NotAllowed (TextNotAllowed), never stored raw.
    private static (string? Value, HomeCheck Check) CleanHome(string? value, int max)
    {
        if (value is null) return (null, HomeCheck.Ok);
        var t = ContactMask.StripInvisible(value).Trim();
        if (t.Length == 0) return (null, HomeCheck.Ok);
        if (t.Length > max) return (null, HomeCheck.Bad);
        return SocialRules.PlaceHasContact(t) ? (null, HomeCheck.NotAllowed) : (t, HomeCheck.Ok);
    }

    // ------------------------------------------------------------------ reads

    private static readonly string MeSql = $"""
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               u."CompletedTripsCount", p."UserId" IS NOT NULL AS "HasProfile",
               p."Gender", p."BirthMonth", p."BirthYear", p."About", p."Interests", p."Skills", p."Places", p."HomeCityName", p."HomeStateName",
               p."IsPrivate", p."MessagePolicy", p."NotifyMessages", p."NotifyFollows", p."NotifyFollowedTrips",
               p."FollowerCount", p."FollowingCount", p."NameChangedAt", p."GuidelinesAcceptedAt", p."GuidelinesVersion",
               (SELECT count(*) FROM "Buddies" b JOIN "Users" bu ON bu."Id" = b."BuddyUserId" AND {ActiveSeeker("bu")} WHERE b."UserId" = @me)::int AS "Buddies",
               (SELECT count(*) FROM "UserBlocks" ub WHERE ub."BlockerId" = @me)::int AS "Blocked"
        FROM "Users" u LEFT JOIN "UserProfiles" p ON p."UserId" = u."Id"
        WHERE u."Id" = @me
        """;

    private static readonly string UsageSql = $"""
        SELECT (SELECT count(*) FROM "Trips" t WHERE t."OwnerUserId" = @me AND t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') AND NOT t."InactiveByPlan")::int
               + (SELECT count(*) FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
                  WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}' AND t."Status" <> '{TripStatuses.Completed}')::int AS "ActiveTrips",
               (SELECT count(*) FROM "Trips" t WHERE t."OwnerUserId" = @me AND {PublicTripReader.LivePost("t")})::int AS "PublicTrips",
               (SELECT count(*) FROM "TripJoinRequests" r WHERE r."RequesterId" = @me AND r."CreatedAt" >= @month)::int AS "JoinRequests",
               (SELECT count(*) FROM "ChatRooms" c WHERE c."RequestedByUserId" = @me AND c."ChargedAt" >= @month)::int AS "NewChats",
               (SELECT count(*) FROM "Follows" f JOIN "Users" fu ON fu."Id" = f."FollowerId" AND {ActiveSeeker("fu")}
                WHERE f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Requested}')::int AS "FollowRequests",
               (SELECT count(*) FROM "Trips" t WHERE t."OwnerUserId" = @me AND t."InactiveByPlan")::int AS "InactiveTrips",
               (SELECT count(*) FROM "Trips" t WHERE t."OwnerUserId" = @me AND t."PausedByPlan" AND NOT t."InactiveByPlan")::int AS "PausedPosts"
        """;

    public async Task<SocialMeDto> GetMeAsync(Guid me, CancellationToken ct = default)
    {
        // PLAN_STATES_SOCIAL 3: the plan reconcile also runs lazily on read (no lock when nothing drifted).
        if (seekerPlans != null) await seekerPlans.EnsureAsync(me, ct);
        var row = (await db.Database.SqlQueryRaw<MeRow>(MeSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        var (limits, usage) = await LimitsAndUsageAsync(me, ct);
        var dto = new SocialMeDto
        {
            GuidelinesAccepted = row is { HasProfile: true } && SocialRules.GuidelinesAccepted(row.GuidelinesAcceptedAt, row.GuidelinesVersion),
            Limits = limits,
            Counts = new SocialMeCountsDto
            {
                FollowRequests = usage.FollowRequests, ChatUnread = await SocialChatService.UnreadAsync(db, me, ct), Blocked = row?.Blocked ?? 0,
            },
            Catalog = new SocialCatalogDto
            {
                Interests = SocialCatalog.Interests, Skills = SocialCatalog.Skills,
                AboutExamples = SocialCatalog.AboutExamples, PostChips = SocialCatalog.PostChips,
            },
        };
        if (row is { HasProfile: true }) dto.Profile = await MyProfileAsync(me, row, usage.PublicTrips, ct);
        return dto;
    }

    public async Task<MyProfileDto?> MyProfileAsync(Guid me, CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQueryRaw<MeRow>(MeSql, Uuid("me", me)).ToListAsync(ct)).FirstOrDefault();
        if (row is not { HasProfile: true }) return null;
        return await MyProfileAsync(me, row, null, ct);
    }

    private async Task<MyProfileDto> MyProfileAsync(Guid me, MeRow row, int? livePosts, CancellationToken ct)
    {
        var posts = await trips.OfOwnerAsync(me, me, ct);
        return new MyProfileDto
        {
            UserId = row.UserId, Name = row.Name, Username = row.Username, PhotoUrl = Face(row.PhotoUrl), Initial = SocialRules.Initial(row.Name),
            NameChangeAllowedAt = SocialRules.NameChangeAllowedAt(row.NameChangedAt, Now),
            Gender = row.Gender ?? string.Empty, BirthMonth = row.BirthMonth ?? 0, BirthYear = row.BirthYear ?? 0,
            HomeCity = row.HomeCityName, HomeState = row.HomeStateName, About = row.About ?? string.Empty,
            Interests = (row.Interests ?? []).ToList(), Skills = (row.Skills ?? []).ToList(), Places = (row.Places ?? []).ToList(),
            IsPrivate = row.IsPrivate ?? false, MessagePolicy = row.MessagePolicy ?? MessagePolicies.Everyone,
            NotifyMessages = row.NotifyMessages ?? true, NotifyFollows = row.NotifyFollows ?? true, NotifyFollowedTrips = row.NotifyFollowedTrips ?? true,
            Counters = new MyCountersDto
            {
                Trips = row.CompletedTripsCount + (livePosts ?? posts.Count), Followers = row.FollowerCount ?? 0,
                Following = row.FollowingCount ?? 0, Buddies = row.Buddies,
            },
            PublicTrips = posts,
        };
    }

    public async Task<(SocialLimitsDto Limits, UsageRow Usage)> LimitsAndUsageAsync(Guid me, CancellationToken ct = default)
    {
        var limits = await seekerLimits.GetAsync(me, ct);
        var now = Now;
        var usage = (await db.Database.SqlQueryRaw<UsageRow>(UsageSql, Uuid("me", me), At("month", SocialMonth.StartUtc(now)),
            Date("today", IstTime.TodayIst(now))).ToListAsync(ct))[0];
        return (new SocialLimitsDto
        {
            IsPlus = limits.IsPlus, CanUpgrade = limits.CanUpgrade, ResetsAt = SocialMonth.ResetsAtUtc(now),
            ActiveTrips = new LimitUseDto { Limit = limits.ActiveTrips, Used = usage.ActiveTrips },
            PublicTrips = new LimitUseDto { Limit = limits.PublicTrips, Used = usage.PublicTrips },
            JoinRequests = new LimitUseDto { Limit = limits.JoinRequestsPerMonth, Used = usage.JoinRequests },
            NewChats = new LimitUseDto { Limit = limits.NewChatsPerMonth, Used = usage.NewChats },
            GenderOnlyTrips = limits.GenderOnlyTrips, NearMe = limits.NearMe,
            InactiveTrips = usage.InactiveTrips, PausedPosts = usage.PausedPosts,
        }, usage);
    }

    // The 429 body of a monthly cap: {limit, used, resetsAt, canUpgrade}.
    public static object CapData(int limit, int used, bool canUpgrade, DateTime nowUtc)
        => new { limit, used, resetsAt = SocialMonth.ResetsAtUtc(nowUtc), canUpgrade };

    // ------------------------------------------------------------------ writes

    // PUT /social/me/onboarding (existing accounts, any role). Insert once; a second call is IdentityLocked.
    public async Task<BuddyResult<MyProfileDto>> OnboardAsync(Guid me, SocialOnboardingRequest request, CancellationToken ct = default)
    {
        var built = Build(me, request, IstTime.TodayIst(Now));
        if (built.Profile is null) return BuddyResult<MyProfileDto>.Fail(built.ErrorType!, built.ErrorData);
        var p = built.Profile;
        var inserted = await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "UserProfiles" ("UserId", "Gender", "BirthMonth", "BirthYear", "About", "Interests", "Skills", "Places",
                                        "HomeCityName", "HomeStateName", "IsPrivate", "MessagePolicy", "NotifyMessages", "NotifyFollows",
                                        "NotifyFollowedTrips", "FollowerCount", "FollowingCount", "CompletedAt", "UpdatedAt")
            SELECT @me, @gender, @bm, @by, @about, @interests, @skills, @places, @city, @state, FALSE, 'Everyone', TRUE, TRUE, TRUE, 0, 0, now(), now()
            WHERE EXISTS (SELECT 1 FROM "Users" u WHERE u."Id" = @me)
            ON CONFLICT ("UserId") DO NOTHING
            """,
            [
                Uuid("me", me), Text("gender", p.Gender), new NpgsqlParameter("bm", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = p.BirthMonth },
                new NpgsqlParameter("by", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = p.BirthYear }, Text("about", p.About),
                Texts("interests", p.Interests), Texts("skills", p.Skills), Texts("places", p.Places),
                NullText("city", p.HomeCityName), NullText("state", p.HomeStateName),
            ], ct);
        if (inserted == 0) return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.IdentityLocked);
        return BuddyResult<MyProfileDto>.Success((await MyProfileAsync(me, ct))!);
    }

    public async Task<BuddyResult<MyProfileDto>> UpdateProfileAsync(Guid me, UpdateSocialProfileRequest r, CancellationToken ct = default)
    {
        if (r.Gender != null || r.BirthMonth != null || r.BirthYear != null) return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.IdentityLocked);
        var profile = await db.Set<UserProfile>().FirstOrDefaultAsync(x => x.UserId == me, ct);
        if (profile is null) return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.ProfileRequired);

        // Every field is checked before anything is written; the name and the profile are then saved together (one SaveChanges).
        string? name = null;
        if (r.Name != null)
        {
            name = ContactMask.StripInvisible(r.Name).Trim();
            if (name.Length == 0 || name.Length > SocialLimits.NameMax) return Fail("name");
            if (ContactMask.HasDigitRun(name) || ContactMask.Apply(name).Masked) return NotAllowed("name");
        }
        if (r.About != null)
        {
            var about = CleanAbout(r.About);
            if (about.Error is { } e) return BuddyResult<MyProfileDto>.Fail(e.ErrorType!, e.ErrorData);
            profile.About = about.Text!;
        }
        if (r.Interests != null)
        {
            var v = SocialRules.CatalogList(r.Interests, SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax);
            if (v is null) return Fail("interests");
            profile.Interests = v;
        }
        if (r.Skills != null)
        {
            var v = SocialRules.CatalogList(r.Skills, SocialCatalog.Skills, SocialLimits.SkillsMin, SocialLimits.SkillsMax);
            if (v is null) return Fail("skills");
            profile.Skills = v;
        }
        if (r.Places != null)
        {
            var v = SocialRules.Places(r.Places);
            if (v is null) return Fail("places");
            if (v.Any(SocialRules.PlaceHasContact)) return NotAllowed("places");
            profile.Places = v;
        }
        if (r.HomeCityName != null)
        {
            var c = CleanHome(r.HomeCityName, SocialLimits.HomeCityMax);
            if (c.Check == HomeCheck.Bad) return Fail("homeCity");
            if (c.Check == HomeCheck.NotAllowed) return NotAllowed("homeCity");
            profile.HomeCityName = c.Value;
        }
        if (r.HomeStateName != null)
        {
            var c = CleanHome(r.HomeStateName, SocialLimits.HomeStateMax);
            if (c.Check == HomeCheck.Bad) return Fail("homeState");
            if (c.Check == HomeCheck.NotAllowed) return NotAllowed("homeState");
            profile.HomeStateName = c.Value;
        }
        var now = Now;
        var user = name != null ? await db.Users.FirstAsync(u => u.Id == me, ct) : null;
        // Spec 23.9: only a different name counts; the stamp and the save commit together (a refused change writes nothing).
        var renamed = user != null && SocialRules.IsNameChange(user.Name, name!);
        await using (var tx = renamed ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            if (renamed)
            {
                var stamp = await NameChangeStamp.TryAsync(db, me, now, ct);
                if (stamp.Outcome == NameStamp.TooSoon)
                {
                    db.ChangeTracker.Clear();
                    return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.NameChangeTooSoon, new { nextAllowedAt = stamp.NextAllowedAt });
                }
                user!.Name = name;
                user.UpdatedAt = now;
            }
            profile.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            if (tx != null) await tx.CommitAsync(ct);
        }
        return BuddyResult<MyProfileDto>.Success((await MyProfileAsync(me, ct))!);

        static BuddyResult<MyProfileDto> Fail(string field) => BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.InvalidProfile, new { field });
        static BuddyResult<MyProfileDto> NotAllowed(string field) => BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.TextNotAllowed, new { field });
    }

    // Private -> public: every waiting follow request becomes accepted in one statement with the counters.
    private static readonly string OpenRequestsSql = $"""
        WITH a AS (
          UPDATE "Follows" SET "Status" = '{FollowStatuses.Accepted}', "AcceptedAt" = now()
          WHERE "FolloweeId" = @me AND "Status" = '{FollowStatuses.Requested}'
          RETURNING "FollowerId"),
        c1 AS (UPDATE "UserProfiles" SET "FollowerCount" = "FollowerCount" + (SELECT count(*) FROM a) WHERE "UserId" = @me),
        c2 AS (UPDATE "UserProfiles" SET "FollowingCount" = "FollowingCount" + 1 WHERE "UserId" IN (SELECT "FollowerId" FROM a))
        SELECT "FollowerId" AS "Value" FROM a
        """;

    // Spec 25: stores the acceptance of the current guidelines version; idempotent (a second call keeps the first time).
    private static readonly string AcceptGuidelinesSql = $"""
        UPDATE "UserProfiles" SET "GuidelinesAcceptedAt" = CASE WHEN "GuidelinesVersion" >= {SocialLimits.GuidelinesVersion} AND "GuidelinesAcceptedAt" IS NOT NULL THEN "GuidelinesAcceptedAt" ELSE now() END,
               "GuidelinesVersion" = GREATEST("GuidelinesVersion", {SocialLimits.GuidelinesVersion})
        WHERE "UserId" = @me
        RETURNING "UserId" AS "Value"
        """;

    public async Task<BuddyResult<bool>> AcceptGuidelinesAsync(Guid me, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Guid>(AcceptGuidelinesSql, Uuid("me", me)).ToListAsync(ct);
        return rows.Count == 0 ? BuddyResult<bool>.Fail(SocialErrorTypes.ProfileRequired) : BuddyResult<bool>.Success(true);
    }

    public async Task<BuddyResult<MyProfileDto>> UpdatePrivacyAsync(Guid me, UpdatePrivacyRequest r, CancellationToken ct = default)
    {
        if (r.MessagePolicy != null && !MessagePolicies.All.Contains(r.MessagePolicy))
            return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.InvalidProfile, new { field = "messagePolicy" });
        var opened = await PgRetry.OnceAsync(db, async c =>
        {
            afterCommit.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var profile = (await db.Set<UserProfile>().FromSqlRaw("""SELECT * FROM "UserProfiles" WHERE "UserId" = @me FOR UPDATE""", Uuid("me", me))
                .ToListAsync(c)).FirstOrDefault();
            if (profile is null) return (List<Guid>?)null;
            var wasPrivate = profile.IsPrivate;
            if (r.IsPrivate is { } isPrivate) profile.IsPrivate = isPrivate;
            if (r.MessagePolicy != null) profile.MessagePolicy = r.MessagePolicy;
            if (r.NotifyMessages is { } nm) profile.NotifyMessages = nm;
            if (r.NotifyFollows is { } nf) profile.NotifyFollows = nf;
            if (r.NotifyFollowedTrips is { } nt) profile.NotifyFollowedTrips = nt;
            profile.UpdatedAt = Now;
            await db.SaveChangesAsync(c);
            List<Guid> accepted = [];
            if (wasPrivate && !profile.IsPrivate)
            {
                accepted = await db.Database.SqlQueryRaw<Guid>(OpenRequestsSql, Uuid("me", me)).ToListAsync(c);
                if (accepted.Count > 0)
                    await db.Database.ExecuteSqlRawAsync(
                        $"""DELETE FROM "UserNotices" WHERE "UserId" = @me AND "Kind" = '{NoticeKinds.FollowRequest}'""", [Uuid("me", me)], c);
            }
            await tx.CommitAsync(c);
            return accepted;
        }, ct, logger);
        if (opened is null) return BuddyResult<MyProfileDto>.Fail(SocialErrorTypes.ProfileRequired);
        if (opened.Count > 0)
        {
            afterCommit.Hint(SocialLiveScopes.FollowRequests, me);
            afterCommit.Hint(SocialLiveScopes.Followers, [me, .. opened]);
            await afterCommit.FlushAsync();
        }
        return BuddyResult<MyProfileDto>.Success((await MyProfileAsync(me, ct))!);
    }

    private static readonly string TravellerSql = $"""
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               u."CompletedTripsCount", p."Gender", p."About", p."Interests", p."Skills", p."Places", p."HomeCityName",
               p."IsPrivate", p."MessagePolicy", p."FollowerCount", p."FollowingCount",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id" AND f."Status" = '{FollowStatuses.Accepted}') AS "IFollow",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = @me AND f."FolloweeId" = u."Id" AND f."Status" = '{FollowStatuses.Requested}') AS "FollowRequested",
               EXISTS (SELECT 1 FROM "Follows" f WHERE f."FollowerId" = u."Id" AND f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}') AS "FollowsMe",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = u."Id") AS "IsBuddy",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = u."Id" AND b."BuddyUserId" = @me) AS "InHisBuddies",
               {JoinBlocks("@me", "@him")} AS "JoinBlocks",
               (SELECT r."Status" FROM "ChatRooms" r
                WHERE r."Kind" = '{ChatKinds.Direct}' AND r."PairLowId" = LEAST(@me, @him) AND r."PairHighId" = GREATEST(@me, @him)
                  AND {SocialChatService.MemberOf("r.\"Id\"")}) AS "RoomStatus",
               (SELECT r."RequestedByUserId" FROM "ChatRooms" r
                WHERE r."Kind" = '{ChatKinds.Direct}' AND r."PairLowId" = LEAST(@me, @him) AND r."PairHighId" = GREATEST(@me, @him)) AS "RoomStarter"
        FROM "Users" u JOIN "UserProfiles" p ON p."UserId" = u."Id"
        WHERE u."Id" = @him AND {ActiveSeeker("u")} AND NOT {BlockedEitherWay("@me", "@him")}
        """;

    // null = 404 PersonNotFound (unknown, not a seeker, paused, no profile, blocked either way).
    public async Task<TravellerProfileDto?> TravellerAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        var r = (await db.Database.SqlQueryRaw<TravellerRow>(TravellerSql, Uuid("me", me), Uuid("him", him)).ToListAsync(ct)).FirstOrDefault();
        if (r is null) return null;
        var self = me == him;
        var locked = r.IsPrivate && !r.IFollow && !self;
        List<PublicTripDto> posts = locked ? [] : await trips.OfOwnerAsync(me, him, ct);
        var mutuals = self ? null : (await MutualsAsync(db, me, [him], ct)).GetValueOrDefault(him);
        return new TravellerProfileDto
        {
            Person = Person(r.UserId, r.Name, r.Username, r.PhotoUrl, locked ? null : r.HomeCityName, r.Gender),
            Gender = r.Gender,
            About = locked ? string.Empty : r.About,
            Interests = locked ? [] : r.Interests.ToList(),
            Skills = locked ? [] : r.Skills.ToList(),
            Places = locked ? [] : r.Places.ToList(),
            Counters = new TravellerCountersDto { Trips = r.CompletedTripsCount + posts.Count, Followers = r.FollowerCount, Following = r.FollowingCount },
            Relation = new TravellerRelationDto
            {
                IFollow = r.IFollow, FollowRequested = r.FollowRequested, FollowsMe = r.FollowsMe, IsBuddy = r.IsBuddy,
                CanAddBuddy = r.FollowsMe && !r.IsBuddy && !self,
                // The preflight's rule (SocialChatService.WithUserAsync): an open room always lets me write, a room I declined never.
                CanMessage = !self && !(r.RoomStatus == ChatRoomStatuses.Declined && r.RoomStarter != me)
                    && (r.RoomStatus != null || (SocialChatService.PolicyAllows(r.MessagePolicy, r.FollowsMe, r.InHisBuddies) && !r.JoinBlocks)),
            },
            Mutuals = mutuals is { Count: > 0 } ? mutuals : null,
            PrivateLocked = locked,
            PublicTrips = posts,
        };
    }

    // PUT /admin/users/{id}/identity: the only path that changes gender / birth, audited in the same transaction. It may also
    // change the name (spec 23.9: any time, and it never sets NameChangedAt, so the user is not locked by it).
    // ProfileRequired = no social profile (404); Under18 = the corrected birth month is under 18 or in the future (400).
    public async Task<BuddyResult<AdminIdentityDto>> AdminSetIdentityAsync(Guid adminId, Guid userId, AdminIdentityRequest r, CancellationToken ct = default)
    {
        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var p = (await db.Set<UserProfile>().FromSqlRaw("""SELECT * FROM "UserProfiles" WHERE "UserId" = @u FOR UPDATE""", Uuid("u", userId))
                .ToListAsync(c)).FirstOrDefault();
            if (p is null) return BuddyResult<AdminIdentityDto>.Fail(SocialErrorTypes.ProfileRequired);
            var year = r.BirthYear ?? p.BirthYear;
            var month = r.BirthMonth ?? p.BirthMonth;
            if (!SocialRules.IsAdult(year, month, IstTime.TodayIst(Now))) return BuddyResult<AdminIdentityDto>.Fail(SocialErrorTypes.Under18);
            var user = await db.Users.FirstAsync(u => u.Id == userId, c);
            var newName = r.Name is null ? null : ContactMask.StripInvisible(r.Name).Trim();
            if (newName is { Length: 0 } || newName?.Length > SocialLimits.NameMax)
                return BuddyResult<AdminIdentityDto>.Fail(SocialErrorTypes.InvalidProfile, new { field = "name" });
            var oldName = user.Name;
            db.Set<ProfileIdentityAudit>().Add(new ProfileIdentityAudit
            {
                UserId = userId, OldGender = p.Gender, OldBirthMonth = p.BirthMonth, OldBirthYear = p.BirthYear,
                NewGender = r.Gender ?? p.Gender, NewBirthMonth = (short)month, NewBirthYear = (short)year,
                OldName = newName != null ? oldName : null, NewName = newName,
                AdminId = adminId, Reason = r.Reason.Trim(), CreatedAt = Now,
            });
            if (newName != null)
            {
                user.Name = newName;
                user.UpdatedAt = Now;
            }
            if (r.Gender != null) p.Gender = r.Gender;
            p.BirthMonth = (short)month;
            p.BirthYear = (short)year;
            p.UpdatedAt = Now;
            // Spec 22: the poster's own gender no longer limits his posts' join rule, so a correction leaves his posts as they are.
            await db.SaveChangesAsync(c);
            await tx.CommitAsync(c);
            logger.LogInformation("admin.action {Action} {AdminId} {TargetId}", "social.identity", adminId, userId);
            return BuddyResult<AdminIdentityDto>.Success(new AdminIdentityDto
            {
                Name = user.Name ?? string.Empty, Gender = p.Gender, BirthMonth = p.BirthMonth, BirthYear = p.BirthYear,
            });
        }, ct, logger);
    }

    private sealed class MeRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public int CompletedTripsCount { get; set; }
        public bool HasProfile { get; set; }
        public string? Gender { get; set; }
        public short? BirthMonth { get; set; }
        public short? BirthYear { get; set; }
        public string? About { get; set; }
        public string[]? Interests { get; set; }
        public string[]? Skills { get; set; }
        public string[]? Places { get; set; }
        public string? HomeCityName { get; set; }
        public string? HomeStateName { get; set; }
        public bool? IsPrivate { get; set; }
        public string? MessagePolicy { get; set; }
        public bool? NotifyMessages { get; set; }
        public bool? NotifyFollows { get; set; }
        public bool? NotifyFollowedTrips { get; set; }
        public int? FollowerCount { get; set; }
        public int? FollowingCount { get; set; }
        public DateTime? NameChangedAt { get; set; }
        public DateTime? GuidelinesAcceptedAt { get; set; }
        public int? GuidelinesVersion { get; set; }
        public int Buddies { get; set; }
        public int Blocked { get; set; }
    }

    public sealed class UsageRow
    {
        public int ActiveTrips { get; set; }
        public int PublicTrips { get; set; }
        public int JoinRequests { get; set; }
        public int NewChats { get; set; }
        public int FollowRequests { get; set; }
        public int InactiveTrips { get; set; }
        public int PausedPosts { get; set; }
    }

    private sealed class TravellerRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public int CompletedTripsCount { get; set; }
        public string Gender { get; set; } = string.Empty;
        public string About { get; set; } = string.Empty;
        public string[] Interests { get; set; } = [];
        public string[] Skills { get; set; } = [];
        public string[] Places { get; set; } = [];
        public string? HomeCityName { get; set; }
        public bool IsPrivate { get; set; }
        public string MessagePolicy { get; set; } = MessagePolicies.Everyone;
        public int FollowerCount { get; set; }
        public int FollowingCount { get; set; }
        public bool IFollow { get; set; }
        public bool FollowRequested { get; set; }
        public bool FollowsMe { get; set; }
        public bool IsBuddy { get; set; }
        public bool InHisBuddies { get; set; }
        public bool JoinBlocks { get; set; }
        public string? RoomStatus { get; set; }
        public Guid? RoomStarter { get; set; }
    }
}

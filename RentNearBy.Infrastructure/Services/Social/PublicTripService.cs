using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

public sealed record PostFields(string WhenKind, int? WithinMonths, DateOnly? StartDate, int Days, string JoinGender, int Seats, string Text);

// Contract 2.4: a public post IS a Trip row. Create (a Draft trip with zero days), publish / edit an own trip, unpublish.
public sealed class PublicTripService(
    ApplicationDbContext db, IUnitOfWork unitOfWork, ITripCityCatalog catalog, ISeekerLimits seekerLimits, PublicTripReader reader,
    SocialAfterCommit afterCommit, TimeProvider clock, ILogger<PublicTripService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // Field rules of the post (contract 2.4). Returns the failing field, else null; text is masked by the caller.
    public static string? FieldError(PostFields f, DateOnly todayIst)
    {
        if (f.WhenKind == SocialRules.WhenKindWithin)
        {
            if (f.WithinMonths is not (>= 1 and <= SocialLimits.WithinMonthsMax)) return "withinMonths";
        }
        else if (f.WhenKind == SocialRules.WhenKindExact)
        {
            if (f.StartDate is not { } d || d < todayIst || d > todayIst.AddDays(SocialLimits.StartDateAheadDays)) return "startDate";
        }
        else return "whenKind";
        if (f.Days is < 1 or > SocialLimits.PostDaysMax) return "days";
        if (!JoinGenders.All.Contains(f.JoinGender)) return "joinGender";
        if (!SocialRules.IsValidSeats(f.Seats)) return "seats";
        var text = (f.Text ?? string.Empty).Trim();
        if (text.Length is 0 or > SocialLimits.PostTextMax) return "text";
        return null;
    }

    // Keeps the post snapshot right whenever the owner saves a public trip (TripHandlers.Update): cities (capped like a new
    // post), start month and the first city's state (blank = the catalog could not tell, the old state stays).
    public static void RefreshPostSnapshot(Trip trip, TripPlan plan, string? firstCityState)
    {
        if (!trip.IsPublic) return;
        trip.PostCityNames = plan.LegCities.Where(n => !string.IsNullOrWhiteSpace(n)).Take(SocialLimits.PostCitiesMax).ToArray();
        if (trip.StartDate is { } start) trip.PostStartMonth = SocialMonth.Key(start);
        if (!string.IsNullOrWhiteSpace(firstCityState)) trip.PostStateName = firstCityState;
    }

    private async Task<BuddyResult<(string Text, string Gender)>> CheckAsync(Guid me, PostFields f, SeekerLimits limits, CancellationToken ct)
    {
        if (FieldError(f, IstTime.TodayIst(Now)) is { } field) return BuddyResult<(string, string)>.Fail(SocialErrorTypes.InvalidPost, new { field });
        var gender = await reader.ViewerGenderAsync(me, ct);
        if (gender is null) return BuddyResult<(string, string)>.Fail(SocialErrorTypes.ProfileRequired);
        // Spec 22: any poster may pick Anyone, Men only or Women only (no check on his own gender); only Women only needs Plus.
        if (SocialRules.NeedsPlus(f.JoinGender) && !limits.GenderOnlyTrips) return BuddyResult<(string, string)>.Fail(SocialErrorTypes.PlusRequired);
        var (masked, hit) = ContactMask.Apply(f.Text.Trim());
        if (hit && ContactMask.IsOnlyMarks(masked)) return BuddyResult<(string, string)>.Fail(SocialErrorTypes.TextNotAllowed, new { field = "text" });
        if (masked.Length > SocialLimits.PostTextMax) masked = masked[..SocialLimits.PostTextMax];
        return BuddyResult<(string, string)>.Success((masked, gender));
    }

    // ------------------------------------------------------------------ POST /social/trips

    public async Task<BuddyResult<PublicTripDto>> CreateAsync(Guid me, CreatePublicTripRequest r, CancellationToken ct = default)
    {
        if (await db.Trips.AsNoTracking().Where(t => t.OwnerUserId == me && t.ClientTripId == r.ClientTripId).Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct) is { } replayId)
            return await AnswerAsync(me, replayId, ct);

        var fields = new PostFields(r.WhenKind, r.WithinMonths, r.StartDate, r.Days, r.JoinGender, r.Seats, r.Text);
        var limits = await seekerLimits.GetAsync(me, ct);
        var checkd = await CheckAsync(me, fields, limits, ct);
        if (!checkd.Ok) return BuddyResult<PublicTripDto>.Fail(checkd.ErrorType!, checkd.ErrorData);

        var legs = await ResolveCitiesAsync(r.CityKeys, ct);
        if (legs.Error is { } cityError) return BuddyResult<PublicTripDto>.Fail(cityError);

        var now = Now;
        var plan = new TripPlan
        {
            Version = TripPlan.CurrentVersion,
            Legs = legs.Legs,
            Days = [],
            Settings = new TripSettings
            {
                StartDate = r.WhenKind == SocialRules.WhenKindExact ? r.StartDate : null,
                MaxDays = r.Days <= TripLimits.MaxDays && r.Days < SocialLimits.DaysTenPlus ? r.Days : null,
            },
        };
        plan.Normalize();
        var name = TripNames.Resolve(null, false, null, null, [], plan.LegCities);
        var first = plan.Legs![0];
        var trip = new Trip
        {
            Id = Guid.NewGuid(), InviteCode = Guid.NewGuid(), OwnerUserId = me, Title = name.Title, NameChosen = name.NameChosen,
            Status = TripStatuses.Draft, ClientTripId = r.ClientTripId, CreatedAt = now, UpdatedAt = now,
            CityKey = first.CityKey, CityName = first.CityName, CityLatitude = first.Latitude, CityLongitude = first.Longitude, DistrictId = first.DistrictId,
            PlanJson = JsonSerializer.Serialize(plan, TripJson.Options), DayCount = 0, SpotCount = 0,
            StartDate = plan.Settings.StartDate, EndDate = plan.EndDate,
        };
        ApplyPost(trip, fields, checkd.Value.Text, now, firstPublish: true);
        trip.PostCityNames = plan.LegCities.ToArray();
        trip.PostStateName = legs.State;

        await unitOfWork.BeginTransactionAsync();
        try
        {
            afterCommit.Clear();
            await BuddyLocks.LockTripSlotsAsync(db, me, ct);
            var slots = await TripSlots.ReadAsync(db, me, limits.ActiveTrips, ct);
            if (TripSlotRules.IsFull(slots.Count, limits.ActiveTrips))
            {
                await unitOfWork.RollbackTransactionAsync();
                return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripLimitReached, slots);
            }
            var live = await LivePostsAsync(me, ct);
            if (live >= limits.PublicTrips)
            {
                await unitOfWork.RollbackTransactionAsync();
                return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.PublicTripLimit,
                    SocialProfileService.CapData(limits.PublicTrips, live, limits.CanUpgrade, now));
            }
            db.Trips.Add(trip);
            await db.SaveChangesAsync(ct);
            await NotifyFollowersAsync(me, trip.Id, trip.JoinGender, ct);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_trips_owner_client" })
        {
            await unitOfWork.RollbackTransactionAsync();
            db.Entry(trip).State = EntityState.Detached;
            var winner = await db.Trips.AsNoTracking().Where(t => t.OwnerUserId == me && t.ClientTripId == r.ClientTripId).Select(t => t.Id).FirstAsync(ct);
            return await AnswerAsync(me, winner, ct);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        afterCommit.Hint(SocialLiveScopes.Posts, me);
        afterCommit.Hint(SocialLiveScopes.Limits, me);
        await afterCommit.FlushAsync(ct);
        return await AnswerAsync(me, trip.Id, ct);
    }

    private async Task<BuddyResult<PublicTripDto>> AnswerAsync(Guid me, Guid tripId, CancellationToken ct)
        => await reader.ByIdAsync(me, tripId, ct) is { } dto
            ? BuddyResult<PublicTripDto>.Success(dto)
            : BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripPostNotFound);

    // 1..4 Atlas cities in order: the first Ready, all in the first city's state, no duplicate (contract 2.4). Catalog snapshot wins.
    private async Task<(List<TripCityLeg> Legs, string? State, string? Error)> ResolveCitiesAsync(IReadOnlyList<long> keys, CancellationToken ct)
    {
        if (keys.Count is 0 or > SocialLimits.PostCitiesMax || keys.Distinct().Count() != keys.Count || keys.Any(k => k <= 0))
            return ([], null, SocialErrorTypes.InvalidCities);
        var legs = new List<TripCityLeg>();
        string? state = null;
        foreach (var key in keys) // batch-loop: at most 4 cached catalog lookups
        {
            var (available, city) = await catalog.FindAsync(key, ct);
            if (!available || city is null) return ([], null, SocialErrorTypes.InvalidCities);
            if (legs.Count == 0)
            {
                if (city.Status != TripSpotStatuses.Ready) return ([], null, SocialErrorTypes.InvalidCities);
                state = city.StateName;
            }
            else if (!string.Equals(city.StateName, state, StringComparison.OrdinalIgnoreCase)) return ([], null, SocialErrorTypes.InvalidCities);
            legs.Add(new TripCityLeg
            {
                Index = legs.Count + 1, CityKey = city.CityKey, CityName = city.Name, Latitude = city.Latitude, Longitude = city.Longitude,
                NotReady = city.Status == TripSpotStatuses.Ready ? null : true,
                DistrictId = await unitOfWork.Districts.FindActiveContainingAsync(city.Latitude, city.Longitude),
            });
        }
        return (legs, string.IsNullOrWhiteSpace(state) ? null : state, null);
    }

    private static void ApplyPost(Trip trip, PostFields f, string maskedText, DateTime nowUtc, bool firstPublish)
    {
        trip.IsPublic = true;
        trip.PausedByPlan = false;
        if (firstPublish || trip.PublishedAt is null) trip.PublishedAt = nowUtc;
        trip.PostText = maskedText;
        trip.PostDays = (short)f.Days;
        trip.JoinGender = f.JoinGender;
        trip.Seats = (short)f.Seats;
        if (f.WhenKind == SocialRules.WhenKindExact)
        {
            trip.PostWindowMonths = null;
            var start = trip.StartDate ?? f.StartDate;
            trip.PostStartMonth = start is { } d ? SocialMonth.Key(d) : null;
        }
        else
        {
            trip.PostWindowMonths = (short)f.WithinMonths!.Value;
            trip.PostStartMonth = trip.StartDate is { } d ? SocialMonth.Key(d) : null;
        }
    }

    private async Task<int> LivePostsAsync(Guid me, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<int>(
            $"""SELECT count(*)::int AS "Value" FROM "Trips" t WHERE t."OwnerUserId" = @me AND {PublicTripReader.LivePost("t")}""",
            Uuid("me", me), Date("today", IstTime.TodayIst(Now))).ToListAsync(ct)).First();

    // One INSERT ... SELECT to the poster's followers who want it (max 2,000); a gender-only post reaches only that gender.
    private static readonly string FollowedPostedSql = $"""
        INSERT INTO "UserNotices" ("Id", "UserId", "Kind", "TripId", "ActorUserId", "RefId", "Amount", "Count", "CreatedAt", "UpdatedAt")
        SELECT gen_random_uuid(), x."FollowerId", '{NoticeKinds.FollowedPosted}', @trip, @me, NULL, NULL, 1, now(), now()
        FROM (
          SELECT f."FollowerId" FROM "Follows" f
          JOIN "UserProfiles" p ON p."UserId" = f."FollowerId" AND p."NotifyFollowedTrips"
          JOIN "Users" u ON u."Id" = f."FollowerId" AND {ActiveSeeker("u")}
          WHERE f."FolloweeId" = @me AND f."Status" = '{FollowStatuses.Accepted}'
            AND (@jg = '{JoinGenders.Anyone}' OR (@jg = '{JoinGenders.Men}' AND p."Gender" = '{SocialGenders.Male}')
                 OR (@jg = '{JoinGenders.Women}' AND p."Gender" = '{SocialGenders.Female}'))
          ORDER BY f."CreatedAt" DESC
          LIMIT {SocialLimits.FollowedPostedMax}) x
        ON CONFLICT ("UserId", "Kind", "TripId") DO NOTHING
        """;

    private Task NotifyFollowersAsync(Guid me, Guid tripId, string joinGender, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(FollowedPostedSql, [Uuid("me", me), Uuid("trip", tripId), Text("jg", joinGender)], ct);

    // ------------------------------------------------------------------ PUT /social/trips/{id}/post

    public async Task<BuddyResult<PublicTripDto>> PutPostAsync(Guid me, Guid tripId, UpdatePublicPostRequest r, CancellationToken ct = default)
    {
        var fields = new PostFields(r.WhenKind, r.WithinMonths, r.StartDate, r.Days, r.JoinGender, r.Seats, r.Text);
        var trip = await db.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.OwnerUserId == me, ct);
        if (trip is null) return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripNotFound);
        if (trip.ArchivedAt != null)
        {
            db.Entry(trip).State = EntityState.Detached;
            return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripArchived);
        }
        if (trip.Status == TripStatuses.Completed) return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripNotPostable);
        var limits = await seekerLimits.GetAsync(me, ct);
        // PLAN_STATES_SOCIAL 3: a trip kept inactive after Plus ended is read-only.
        if (trip.InactiveByPlan)
        {
            db.Entry(trip).State = EntityState.Detached;
            return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.TripInactiveByPlan, new { limit = limits.ActiveTrips });
        }
        // An exact date already known on the trip wins over the one sent.
        var effective = trip.StartDate != null && fields.WhenKind == SocialRules.WhenKindExact ? fields with { StartDate = trip.StartDate } : fields;
        var checkd = await CheckAsync(me, effective, limits, ct);
        if (!checkd.Ok) return BuddyResult<PublicTripDto>.Fail(checkd.ErrorType!, checkd.ErrorData);

        var now = Now;
        var firstPublish = !trip.IsPublic;
        var plan = TripPlanReader.Read(trip);
        if (effective.WhenKind == SocialRules.WhenKindExact && trip.StartDate is null && effective.StartDate is { } start)
        {
            plan.Settings.StartDate = start;
            plan.Version = TripPlan.CurrentVersion;
            trip.PlanJson = JsonSerializer.Serialize(plan, TripJson.Options);
            trip.StartDate = start;
            trip.EndDate = plan.EndDate;
        }
        if (firstPublish)
        {
            trip.PostCityNames = plan.LegCities.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            var (available, city) = await catalog.FindAsync(trip.CityKey, ct);
            trip.PostStateName = available && city is not null && !string.IsNullOrWhiteSpace(city.StateName) ? city.StateName : trip.PostStateName;
        }
        ApplyPost(trip, effective, checkd.Value.Text, now, firstPublish);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            afterCommit.Clear();
            if (firstPublish)
            {
                await BuddyLocks.LockTripSlotsAsync(db, me, ct);
                var live = await LivePostsAsync(me, ct);
                if (live >= limits.PublicTrips)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    db.Entry(trip).State = EntityState.Detached;
                    return BuddyResult<PublicTripDto>.Fail(SocialErrorTypes.PublicTripLimit,
                        SocialProfileService.CapData(limits.PublicTrips, live, limits.CanUpgrade, now));
                }
            }
            await db.SaveChangesAsync(ct);
            if (firstPublish) await NotifyFollowersAsync(me, trip.Id, trip.JoinGender, ct);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        afterCommit.Hint(SocialLiveScopes.Posts, me);
        if (firstPublish) afterCommit.Hint(SocialLiveScopes.Limits, me);
        await afterCommit.FlushAsync(ct);
        return await AnswerAsync(me, trip.Id, ct);
    }

    // ------------------------------------------------------------------ DELETE post (owner) and admin unpublish

    private static readonly string UnpublishSql = $"""
        WITH t AS (
          UPDATE "Trips" SET "IsPublic" = FALSE, "PausedByPlan" = FALSE
          WHERE "Id" = @trip AND (@owner::uuid IS NULL OR "OwnerUserId" = @owner::uuid)
          RETURNING "Id", "OwnerUserId"),
        r AS (
          UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Expired}', "EndedAt" = now()
          WHERE "TripId" IN (SELECT "Id" FROM t) AND "Status" = '{JoinRequestStatuses.Waiting}'
          RETURNING "Id", "RequesterId"),
        n AS (DELETE FROM "UserNotices" WHERE "Kind" = '{NoticeKinds.JoinRequest}' AND "TripId" IN (SELECT "Id" FROM r))
        SELECT (SELECT t."OwnerUserId" FROM t) AS "OwnerUserId", ARRAY(SELECT r."RequesterId" FROM r) AS "Requesters"
        """;

    // ownerId null = the admin route. false = no such trip (of that owner).
    public async Task<bool> UnpublishAsync(Guid? ownerId, Guid tripId, CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQueryRaw<UnpublishRow>(UnpublishSql, Uuid("trip", tripId), NullUuid("owner", ownerId)).ToListAsync(ct)).First();
        if (row.OwnerUserId is not { } owner) return false;
        afterCommit.Clear();
        afterCommit.Hint(SocialLiveScopes.Posts, owner);
        afterCommit.Hint(SocialLiveScopes.JoinRequests, owner);
        if (row.Requesters is { Length: > 0 } requesters) afterCommit.Hint(SocialLiveScopes.MyJoinRequests, requesters);
        await afterCommit.FlushAsync(ct);
        logger.LogInformation("social.trip.unpublished {TripId} {ByAdmin}", tripId, ownerId is null);
        return true;
    }

    private sealed class UnpublishRow
    {
        public Guid? OwnerUserId { get; set; }
        public Guid[]? Requesters { get; set; }
    }
}

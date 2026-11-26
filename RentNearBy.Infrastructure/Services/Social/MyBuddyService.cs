using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 1.4 / 2.3: My Buddies is MY list picked from MY followers (one row, no request, the other person is never told).
public sealed class MyBuddyService(ApplicationDbContext db, IBuddyLiveHints hints, ILogger<MyBuddyService> logger)
{
    private static readonly string StateSql = $"""
        SELECT EXISTS (SELECT 1 FROM "Users" u JOIN "UserProfiles" p ON p."UserId" = u."Id"
                       WHERE u."Id" = @him AND {ActiveSeeker("u")} AND NOT {BlockedEitherWay("@me", "@him")}) AS "Visible",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = @him) AS "Already",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me)::int AS "Count"
        """;

    public async Task<BuddyResult<IsBuddyDto>> AddAsync(Guid me, Guid him, CancellationToken ct = default)
    {
        if (me == him) return BuddyResult<IsBuddyDto>.Fail(SocialErrorTypes.PersonNotFound);
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            // Lock order: sender (the cap is a count under it, contract D6) -> pair.
            await BuddyLocks.LockSenderAsync(db, me, c);
            await BuddyLocks.LockPairAsync(db, me, him, c);
            var s = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Uuid("me", me), Uuid("him", him)).ToListAsync(c))[0];
            if (!s.Visible) return BuddyResult<IsBuddyDto>.Fail(SocialErrorTypes.PersonNotFound);
            if (s.Already) return BuddyResult<IsBuddyDto>.Success(new IsBuddyDto { IsBuddy = true });
            if (s.Count >= BuddyLimits.MaxBuddies) return BuddyResult<IsBuddyDto>.Fail(SocialErrorTypes.BuddyLimit);
            var inserted = await InsertBuddyCappedAsync(db, me, him, c);
            if (inserted == 0) return BuddyResult<IsBuddyDto>.Fail(SocialErrorTypes.NotFollower);
            await tx.CommitAsync(c);
            return BuddyResult<IsBuddyDto>.Success(new IsBuddyDto { IsBuddy = true });
        }, ct, logger);
        if (result.Ok) await hints.BuddiesChangedAsync([me]);
        return result;
    }

    private sealed class StateRow
    {
        public bool Visible { get; set; }
        public bool Already { get; set; }
        public int Count { get; set; }
    }
}

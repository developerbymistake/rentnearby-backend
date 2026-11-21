using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be4;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Each new kind passes the check constraint and is upserted in place:
// one row per user + kind + trip, Count grows until read, a read restarts it at 1, the latest actor/ref/amount win.
public class InboxRowKindsIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationTheory]
    [InlineData(NoticeKinds.BuddyAccepted, false)]
    [InlineData(NoticeKinds.NewBuddy, false)]
    [InlineData(NoticeKinds.TripJoined, true)]
    [InlineData(NoticeKinds.PlanUpdated, true)]
    [InlineData(NoticeKinds.ExpenseAdded, true)]
    [InlineData(NoticeKinds.ShareChanged, true)]
    public async Task New_kind_is_upserted_in_place(string kind, bool onTrip)
    {
        Guid user, a1, a2, trip;
        await using (var seed = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(seed, 3);
            (user, a1, a2) = (p[0], p[1], p[2]);
            trip = onTrip ? await BuddySeed.TripAsync(seed, a1) : NoticeKinds.NoTrip;
        }
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        await using (var c = CreateContext())
        {
            var w = new NoticeWriter(c);
            await w.UpsertAsync([new NoticeUpsert(user, kind, trip, a1, r1, 6400)]);
            await w.UpsertAsync([new NoticeUpsert(user, kind, trip, a2, r2, 6166)]);
        }
        var row = await SingleAsync(user, kind, trip);
        Assert.Equal(2, row.Count);
        Assert.Equal((a2, r2, 6166), (row.ActorUserId!.Value, row.RefId!.Value, row.Amount!.Value));
        var firstUpdated = row.UpdatedAt;

        await using (var c = CreateContext())
        {
            await new NoticeReader(c).MarkReadAsync(row.Id, user);
            await new NoticeWriter(c).UpsertAsync([new NoticeUpsert(user, kind, trip, a1, r1, 100)]);
        }
        row = await SingleAsync(user, kind, trip);
        Assert.Equal(1, row.Count);
        Assert.Null(row.ReadAt);
        Assert.True(row.UpdatedAt >= firstUpdated);
        Assert.Equal(100, row.Amount);
    }

    [IntegrationFact]
    public async Task Trip_delete_cleanup_removes_every_new_trip_kind()
    {
        Guid user, owner, trip;
        await using (var seed = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(seed, 2);
            (user, owner) = (p[0], p[1]);
            trip = await BuddySeed.TripAsync(seed, owner);
            await BuddySeed.NoticesAsync(seed, user,
                [(NoticeKinds.TripJoined, trip), (NoticeKinds.PlanUpdated, trip), (NoticeKinds.ExpenseAdded, trip), (NoticeKinds.ShareChanged, trip)]);
        }
        await using (var c = CreateContext()) await new NoticeWriter(c).DeleteForTripAsync(trip, [NoticeKinds.TripDeleted]);
        await using var check = CreateContext();
        Assert.False(await check.Set<UserNotice>().AnyAsync(n => n.TripId == trip));
    }

    private async Task<UserNotice> SingleAsync(Guid user, string kind, Guid trip)
    {
        await using var c = CreateContext();
        return Assert.Single(await c.Set<UserNotice>().AsNoTracking().Where(n => n.UserId == user && n.Kind == kind && n.TripId == trip).ToListAsync());
    }
}

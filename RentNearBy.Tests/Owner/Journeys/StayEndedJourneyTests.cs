using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-ST-1 step Ended and JT-B-ST-2 (FB-16, FB-18, FB-19, FB-35, FB-38, FB-39, FB-40, FB-43, FB-54): what a Free owner keeps after
// Plus ends. Pure parts and source guards run here.
public class StayEndedJourneyTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);
    private static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    // FB-16: every owner write that returns the stay answers the hub shape (meters, hidden rows), never the slim DTO.
    [Fact]
    public void Stay_owner_writes_answer_the_hub_shape()
    {
        var src = Src("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        Assert.DoesNotContain("OkResponse(await ToOwnerDtoAsync(", src);
        foreach (var name in new[] { "UpsertRules(", "Submit(", "UpdateAccepting(", "UpdateShareDetails(", "UpdateRoomPrice(", "UpsertRooms(" })
            Assert.Contains("ToHubDtoAsync(stay, unitOfWork, hub", Method(src, "public static async Task<IResult> " + name));
    }

    // FB-54 + FB-19: the photo delete runs under the owner and stay locks with the "another visible photo" check in the statement,
    // then refills a freed slot from the hidden photos in the same transaction.
    [Fact]
    public void Photo_delete_is_one_guarded_statement_under_the_locks_and_refills()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"), "public static async Task<IResult> DeletePhoto(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("slots.LockOwnerAsync") < At("OwnerLocks.LockStayAsync"));
        Assert.True(At("OwnerLocks.LockStayAsync") < At("DeletePhotoSql"));
        Assert.True(At("DeletePhotoSql") < At("planRows.ApplyForOwnerAsync"));
        Assert.True(At("planRows.ApplyForOwnerAsync") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("photoService.DeletePhotoAsync"));
        Assert.DoesNotContain("db.StayPhotos.Remove", body);
        Assert.Contains("NOT p.\"HiddenByPlan\"", StayHandlers.DeletePhotoSql);
        Assert.Contains("o.\"Id\" <> @p AND NOT o.\"HiddenByPlan\"", StayHandlers.DeletePhotoSql);
    }

    // FB-19 + FB-38: room save re-reads the limits under the locks and refills after a visible room was removed.
    [Fact]
    public void Room_save_checks_the_limit_under_the_locks_and_refills()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"), "public static async Task<IResult> UpsertRooms(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("slots.LockOwnerAsync") < At("OwnerLocks.LockStayAsync"));
        var locked = At("OwnerLocks.LockStayAsync");
        Assert.True(body.IndexOf("entitlements.GetLimitsAsync", locked, StringComparison.Ordinal) > locked);
        Assert.True(At("planRows.ApplyForOwnerAsync") < At("CommitTransactionAsync"));
    }

    // FB-38: the plan value of every limited create is read again after the listing lock.
    [Theory]
    [InlineData("RentNearBy.Infrastructure/Services/Owner/Be1/OwnerCalendarWriter.cs", "public async Task<Be1Result<CalendarEntryWrite>> CreateAsync(", "OwnerLocks.LockListingAsync")]
    [InlineData("RentNearBy.Infrastructure/Services/Owner/Be1/BlockedRangeService.cs", "public async Task<Be1Result<BlockedRangeWrite>> CreateAsync(", "OwnerLocks.LockStayAsync")]
    [InlineData("RentNearBy.Infrastructure/Services/Owner/Be2/StaySpecialPriceService.cs", "public async Task<Be2Result<SpecialPriceWriteDto>> CreateAsync(", "LockAsync(ctx.ListingId")]
    [InlineData("RentNearBy.Infrastructure/Services/Owner/Be2/StaySpecialPriceService.cs", "public async Task<Be2Result<SpecialPriceWriteDto>> MakeLiveAsync(", "LockAsync(ctx.ListingId")]
    public void Limited_creates_read_the_plan_under_the_lock(string file, string signature, string lockCall)
    {
        var body = Method(Src(file.Split('/')), signature);
        var locked = body.IndexOf(lockCall, StringComparison.Ordinal);
        Assert.True(locked >= 0);
        Assert.True(body.IndexOf("WithLimitsUnderLockAsync", StringComparison.Ordinal) > locked);
    }

    // FB-35: special price writes while under review answer StayLocked (409), delete stays allowed.
    [Fact]
    public void Special_price_writes_are_locked_during_review()
    {
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.StayLocked));
        Assert.Equal("Your stay is under review.", OwnerErrorTypes.MessageOf(OwnerErrorTypes.StayLocked));
        var src = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        foreach (var name in new[] { "CreateAsync(", "UpdateAsync(", "PauseAsync(", "MakeLiveAsync(" })
            Assert.Contains("UnderReview(lockRow)", Method(src, "public async Task<Be2Result<SpecialPriceWriteDto>> " + name));
        Assert.DoesNotContain("UnderReview(lockRow)", Method(src, "public async Task<Be2Result<bool>> DeleteAsync("));
    }

    // FB-18: delete re-ranks the plan-paused rules; make-live also clears the owner's own pause of the chosen rule.
    [Fact]
    public void Special_price_delete_re_ranks_and_make_live_unpauses()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        var delete = Method(src, "public async Task<Be2Result<bool>> DeleteAsync(");
        Assert.True(delete.IndexOf("PlanRows.ApplySpecialPricesAsync", StringComparison.Ordinal) < delete.IndexOf("CommitAsync", StringComparison.Ordinal));
        Assert.Contains("\"IsPaused\" = CASE WHEN t.\"Id\" = {id} THEN false ELSE t.\"IsPaused\" END", Method(src, "public async Task<Be2Result<SpecialPriceWriteDto>> MakeLiveAsync("));
    }

    // FB-43: past start has its own line; a date move without a festival drops the old label.
    [Fact]
    public void Special_price_start_lines_and_festival_drop()
    {
        Assert.Equal(OwnerErrorTypes.SpecialPriceStartPast, StaySpecialPriceRules.Invalid(SpecialPriceDirections.Lower, 10, Today.AddDays(-1), Today.AddDays(2), Today));
        Assert.Equal(OwnerErrorTypes.SpecialPriceStartTooLate, StaySpecialPriceRules.Invalid(SpecialPriceDirections.Lower, 10, Today.AddDays(200), Today.AddDays(202), Today));
        Assert.Null(StaySpecialPriceRules.Invalid(SpecialPriceDirections.Lower, 10, Today.AddDays(-3), Today.AddDays(2), Today, keepFrom: Today.AddDays(-3)));
        Assert.True(StaySpecialPriceRules.DropsFestival(null, Today, Today.AddDays(2), Today.AddDays(1), Today.AddDays(2)));
        Assert.False(StaySpecialPriceRules.DropsFestival(null, Today, Today.AddDays(2), Today, Today.AddDays(2)));
        Assert.False(StaySpecialPriceRules.DropsFestival(Guid.NewGuid(), Today, Today.AddDays(2), Today.AddDays(5), Today.AddDays(6)));
    }

    // FB-39 (D-12): the reconcile runs on the first tick of each IST day; the expiry pass every 5 minutes.
    [Fact]
    public void Daily_reconcile_follows_the_ist_day()
    {
        var beforeMidnightIst = new DateTime(2026, 11, 25, 18, 25, 0, DateTimeKind.Utc); // 23:55 IST on the 25th
        var afterMidnightIst = new DateTime(2026, 11, 25, 18, 35, 0, DateTimeKind.Utc);  // 00:05 IST on the 26th
        Assert.True(PlanLifecycleService.ReconcileDue(null, beforeMidnightIst));
        Assert.False(PlanLifecycleService.ReconcileDue(new DateOnly(2026, 11, 25), beforeMidnightIst));
        Assert.True(PlanLifecycleService.ReconcileDue(new DateOnly(2026, 11, 25), afterMidnightIst));
        Assert.Equal(TimeSpan.FromMinutes(5), PlanLifecycleService.Interval);
        Assert.Equal(TimeSpan.FromHours(1), PlanLifecycleService.ReminderEvery);
    }

    // FB-39 + FB-40: the words follow the reason; special-price-only losses lead to the special prices list.
    [Fact]
    public void Limits_push_words_follow_the_reason()
    {
        Assert.Equal("Plans have started", StayOwnerPushCopy.PlansStarted(User, 1, 0, Today).Title);
        Assert.Equal("Plus has ended", StayOwnerPushCopy.PlansStarted(User, 1, 0, Today, 0, plansStartedToday: false).Title);
        Assert.Equal("Plus has ended", TaxiDriverPushCopy.PlansStarted(User, 2, Today, plansStartedToday: false).Title);

        var rules = StayOwnerPushCopy.PlansStarted(User, 0, 0, Today, 3, plansStartedToday: false);
        Assert.Equal("3 special prices are paused on Free. Nothing is deleted.", rules.Body);
        Assert.Equal(UserNotificationRoutes.StaySpecialPrices, rules.Route);
        Assert.Equal("1 special price is paused on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(User, 0, 0, Today, 1).Body);
        Assert.Equal(UserNotificationRoutes.StayPlanVisible, StayOwnerPushCopy.PlansStarted(User, 1, 0, Today, 3).Route);
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class StayEndedJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    // FB-54: two parallel deletes of the last two visible photos of a live stay: one succeeds, the other answers 0 rows.
    [IntegrationFact]
    public async Task Parallel_photo_deletes_keep_one_visible_photo()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> photos;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            photos = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2);
        }

        async Task<int> DeleteAsync(Guid photoId)
        {
            await using var c = CreateContext();
            await using var tx = await c.Database.BeginTransactionAsync();
            await c.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "StayListings" WHERE "Id" = @s FOR UPDATE""",
                new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = owner.ListingId });
            var n = await c.Database.ExecuteSqlRawAsync(StayHandlers.DeletePhotoSql,
                new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = photoId },
                new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = owner.ListingId },
                new NpgsqlParameter("approved", NpgsqlDbType.Varchar) { Value = StayStatuses.Approved });
            await tx.CommitAsync();
            return n;
        }

        var results = await Task.WhenAll(DeleteAsync(photos[0].Id), DeleteAsync(photos[1].Id));
        Assert.Equal(1, results.Sum());
        await using var check = CreateContext();
        Assert.Equal(1, await check.StayPhotos.CountAsync(p => p.StayListingId == owner.ListingId));
    }

    // FB-54: a hidden photo is never deleted by the owner route (it is chosen in "Choose what stays").
    [IntegrationFact]
    public async Task Hidden_photo_is_not_deleted_by_the_owner_route()
    {
        OwnerSeed.OwnerRow owner;
        List<StayPhoto> photos;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            photos = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 2, hidden: 1);
        }
        var hidden = photos.Single(p => p.HiddenByPlan);
        await using var db = CreateContext();
        var n = await db.Database.ExecuteSqlRawAsync(StayHandlers.DeletePhotoSql,
            new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = hidden.Id },
            new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = owner.ListingId },
            new NpgsqlParameter("approved", NpgsqlDbType.Varchar) { Value = StayStatuses.Approved });
        Assert.Equal(0, n);
    }
}

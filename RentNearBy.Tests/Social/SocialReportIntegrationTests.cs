using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Social;

// Contract 2.7 / 2.8: message report with a snapshot around the reported seq, trip post report, admin snapshot read, admin
// unpublish, and the admin Reject of the three social report types. Needs RENTNEARBY_TEST_DB; skipped without it.
public class SocialReportIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly Guid RudeReason = Guid.Parse("c2000000-0000-0000-0000-000000000008");

    private sealed class RecordingBlocks : IUserBlockService
    {
        public List<(Guid Blocker, Guid Blocked)> Blocked { get; } = [];

        public Task<BlockOutcome> BlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
        {
            Blocked.Add((blockerId, blockedId));
            return Task.FromResult(new BlockOutcome(BlockResult.Done, []));
        }

        public Task<BlockOutcome> UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
            => Task.FromResult(new BlockOutcome(BlockResult.Done, []));
    }

    private static SocialReportService Reports(ApplicationDbContext ctx, IUserBlockService? blocks = null)
        => new(ctx, new PublicTripReader(ctx, TimeProvider.System), blocks ?? new RecordingBlocks(), NullLogger<SocialReportService>.Instance);

    private static SocialChatService Chat(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
            NullLogger<SocialChatService>.Instance);

    private static PublicTripService Posts(ApplicationDbContext ctx) =>
        new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), TimeProvider.System, NullLogger<PublicTripService>.Instance);

    // The admin seam of the reject route: no admin id claim, so the resolver column stays empty (no Admins row needed).
    private static Task<Microsoft.AspNetCore.Http.IResult> RejectAsync(ApplicationDbContext ctx, Guid reportId)
        => AdminHandlers.RejectReport(reportId, new RejectGoLiveRequest("Nothing against the rules"), new RejectGoLiveRequestValidator(),
            new ClaimsPrincipal(new ClaimsIdentity()), new UnitOfWork(ctx), ctx, new ServiceCollection().BuildServiceProvider(), null!, null!);

    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        if (!await ctx.ReportReasons.AnyAsync(r => r.Id == RudeReason))
        {
            ctx.ReportReasons.Add(new ReportReason { Id = RudeReason, Scope = ReportScopes.User, Name = "Rude or unsafe", SortOrder = 8, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        var ids = await BuddySeed.UsersAsync(ctx, n);
        foreach (var id in ids) await SocialSeed.ProfileAsync(ctx, id);
        return ids;
    }

    private async Task<Guid> ReportIdAsync(Guid reporter, Guid listing)
    {
        await using var ctx = CreateContext();
        return await ctx.ListingReports.Where(r => r.ReporterUserId == reporter && r.ListingId == listing).Select(r => r.Id).SingleAsync();
    }

    [IntegrationFact]
    public async Task Message_report_keeps_a_snapshot_with_the_reported_line_and_admin_can_read_and_reject_it()
    {
        var p = await PeopleAsync(2);
        Guid room;
        await using (var ctx = CreateContext())
        {
            room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali?")).Value!.Room.RoomId;
            Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Send me money first")).Ok);
            Assert.True((await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), "No thanks")).Ok);
            Assert.Equal(SocialErrorTypes.MessageGone,
                (await Reports(ctx).ReportAsync(p[0], new SocialReportRequest { Scope = SocialReportTypes.Message, RoomId = room, Seq = 3, ReasonId = RudeReason })).ErrorType);
            var blocks = new RecordingBlocks();
            var reported = await Reports(ctx, blocks).ReportAsync(p[0],
                new SocialReportRequest { Scope = SocialReportTypes.Message, RoomId = room, Seq = 2, ReasonId = RudeReason, AlsoBlock = true });
            Assert.True(reported.Ok, reported.ErrorType);
            Assert.True(reported.Value!.Blocked);
            Assert.Equal((p[0], p[1]), Assert.Single(blocks.Blocked));
        }

        var reportId = await ReportIdAsync(p[0], room);
        await using (var ctx = CreateContext())
        {
            var report = await ctx.ListingReports.AsNoTracking().SingleAsync(r => r.Id == reportId);
            Assert.Equal((SocialReportTypes.Message, p[1], (long?)2), (report.ListingType, report.ReportedUserId, report.MessageSeq));
            var snapshot = await Reports(ctx).AdminSnapshotAsync(Guid.NewGuid(), reportId);
            Assert.NotNull(snapshot);
            Assert.Equal(new long[] { 1, 2, 3 }, snapshot!.Messages.Select(m => m.Seq));
            Assert.Equal(2, snapshot.Messages.Single(m => m.IsReported).Seq);

            Assert.Equal(200, StatusOf(await RejectAsync(ctx, reportId)));
        }
        await using var check = CreateContext();
        var closed = await check.ListingReports.AsNoTracking().SingleAsync(r => r.Id == reportId);
        Assert.Equal(("Resolved", "Rejected"), (closed.Status, closed.ResolutionAction));
        Assert.True(await check.Set<MessageSnapshot>().AnyAsync(s => s.ReportId == reportId));
    }

    [IntegrationFact]
    public async Task Trip_post_report_files_once_and_admin_unpublish_and_reject_work()
    {
        var p = await PeopleAsync(2);
        Guid tripId;
        await using (var ctx = CreateContext())
        {
            tripId = (await Posts(ctx).CreateAsync(p[0], new CreatePublicTripRequest
            {
                ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin, WithinMonths = 1,
                Days = 2, JoinGender = JoinGenders.Anyone, Seats = 1, Text = "Lake walk and momos.",
            })).Value!.TripId;
            var request = new SocialReportRequest { Scope = SocialReportTypes.TripPost, TripId = tripId, ReasonId = RudeReason, Note = "Feels like a scam" };
            Assert.True((await Reports(ctx).ReportAsync(p[1], request)).Ok);
            Assert.True((await Reports(ctx).ReportAsync(p[1], request)).Ok);
            Assert.Equal(SocialErrorTypes.TripPostNotFound, (await Reports(ctx).ReportAsync(p[0], request)).ErrorType);
        }
        var reportId = await ReportIdAsync(p[1], tripId);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(1, await ctx.ListingReports.CountAsync(r => r.ListingId == tripId && r.Status == "Pending"));
            Assert.True(await Posts(ctx).UnpublishAsync(null, tripId));
            Assert.False((await ctx.Trips.AsNoTracking().SingleAsync(t => t.Id == tripId)).IsPublic);
            Assert.Equal(200, StatusOf(await RejectAsync(ctx, reportId)));
            Assert.Equal(400, StatusOf(await RejectAsync(ctx, reportId)));
        }
    }

    [IntegrationFact]
    public async Task Person_report_is_rejected_without_a_takedown()
    {
        var p = await PeopleAsync(2);
        var reportId = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            ctx.ListingReports.Add(new ListingReport
            {
                Id = reportId, ListingId = p[1], ListingType = ReportScopes.User, ReporterUserId = p[0], ReporterName = "Person 1",
                ReportedUserId = p[1], ReportedName = "Person 2", ReasonId = RudeReason, Details = "Rude in chat", Status = "Pending",
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            Assert.Equal(200, StatusOf(await RejectAsync(ctx, reportId)));
        }
        await using var check = CreateContext();
        var closed = await check.ListingReports.AsNoTracking().SingleAsync(r => r.Id == reportId);
        Assert.Equal(("Resolved", "Rejected"), (closed.Status, closed.ResolutionAction));
        Assert.True(await check.Users.AsNoTracking().Where(u => u.Id == p[1]).Select(u => u.IsActive).SingleAsync());
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Matrix L15 on a real database: two adds at the limit at the same time (only one wins), expire while the owner is saving, and
// delete of an inactive row that a re-buy just restored. Each call runs on its own context, as two phones would. Needs
// RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on the build machine.
public class SubscriptionRaceIntegrationTests(TestDatabase db) : StayLifecycleDbBase(db)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0];

    private static ClaimsPrincipal Principal(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static int StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private static IFormFile Photo() => new FormFile(new MemoryStream(Jpeg), 0, Jpeg.Length, "photo", "new.jpg");

    private static IServiceProvider NoServices() => new ServiceCollection().BuildServiceProvider();

    private async Task<int> UploadAsync(Guid userId)
    {
        await using var c = CreateContext();
        return StatusOf(await StayHandlers.UploadPhoto(Photo(), Principal(userId), new UnitOfWork(c), c, new RecordingPhotoService(),
            Entitlements(c, true), NoServices()));
    }

    private async Task<int> SaveRoomsAsync(Guid userId, IEnumerable<StayRoomInput> rooms)
    {
        await using var c = CreateContext();
        var entitlements = Entitlements(c, true);
        var planRows = Reconcile(c, true, new RecordingNotifier());
        var slots = new PlanSlotService(c, entitlements, new Be2.FixedPlanCatalog(OwnerPlanCatalog.Seeded()),
            new Be2.FixedPaymentFeature(PaymentFeatureStates.On()), planRows);
        return StatusOf(await StayHandlers.UpsertRooms(new UpsertStayRoomsRequest { Rooms = rooms.ToList() }, Principal(userId),
            new UpsertStayRoomsRequestValidator(), new UnitOfWork(c), c, entitlements, new StayHubReader(c, entitlements, TimeProvider.System),
            slots, planRows, NoServices()));
    }

    private static StayRoomInput Input(StayRoom r, int price) => new()
    {
        Id = r.Id, PricePerNight = price, MaxGuests = r.MaxGuests, RoomName = r.RoomName, Meals = r.Meals, Floor = r.Floor,
    };

    [IntegrationFact]
    public async Task Photos_two_uploads_at_the_free_limit_at_once_give_one_201_and_one_409()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
        }

        var codes = await Task.WhenAll(UploadAsync(owner.UserId), UploadAsync(owner.UserId));
        Assert.Single(codes, s => s == 409);
        Assert.Single(codes, s => s is >= 200 and < 300);
        Assert.Equal(2, (await PhotosOfAsync(owner.ListingId)).Count(p => !p.HiddenByPlan));
    }

    [IntegrationFact]
    public async Task Rooms_two_saves_at_the_free_limit_at_once_never_pass_the_limit()
    {
        OwnerSeed.OwnerRow owner;
        List<StayRoom> rooms;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 1);
        }
        StayRoomInput New(string name) => new() { PricePerNight = 2500, MaxGuests = 2, RoomName = name, Meals = rooms[0].Meals };

        var codes = await Task.WhenAll(
            SaveRoomsAsync(owner.UserId, [Input(rooms[0], 3000), New(StayRoomNames.Suite)]),
            SaveRoomsAsync(owner.UserId, [Input(rooms[0], 3000), New(StayRoomNames.Premium)]));
        Assert.Contains(200, codes);
        Assert.All(codes, s => Assert.True(s is 200 or 409, $"status {s}"));
        var now = await RoomsOfAsync(owner.ListingId);
        Assert.Equal(2, now.Count);
        Assert.DoesNotContain(now, r => r.HiddenByPlan);
    }

    [IntegrationFact]
    public async Task Rooms_save_while_plus_expires_ends_with_the_free_rows_either_way()
    {
        OwnerSeed.OwnerRow owner;
        List<StayRoom> rooms;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 6);
        }
        var plus = await BuyPlusAsync(owner.UserId);
        await SetPlanAsync(plus, UserPlanStatuses.Expired);

        var save = SaveRoomsAsync(owner.UserId, rooms.Select(r => Input(r, 3200)));
        var expire = ApplyAsync(owner.UserId, true);
        await Task.WhenAll(save, expire);
        Assert.True(save.Result is 200 or 409 or 400, $"status {save.Result}");

        // Whichever commits first, the expiry wins: six rooms kept, the last two visible.
        await ApplyAsync(owner.UserId, true);
        var now = await RoomsOfAsync(owner.ListingId);
        Assert.Equal(6, now.Count);
        Assert.Equal([rooms[4].Id, rooms[5].Id], now.Where(r => !r.HiddenByPlan).OrderBy(r => r.RoomIndex).Select(r => r.Id).ToList());
    }

    [IntegrationFact]
    public async Task Rooms_delete_of_an_inactive_room_that_a_rebuy_restored_is_refused_and_keeps_it()
    {
        OwnerSeed.OwnerRow owner;
        List<StayRoom> rooms;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 3);
        }
        var plus = await BuyPlusAsync(owner.UserId);
        await SetPlanAsync(plus, UserPlanStatuses.Expired);
        await ApplyAsync(owner.UserId, true);
        var hidden = (await RoomsOfAsync(owner.ListingId)).Single(r => r.HiddenByPlan);

        // The re-buy restores the room after the phone loaded it as inactive.
        await BuyPlusAsync(owner.UserId);
        await ApplyAsync(owner.UserId, true);

        await using (var c = CreateContext())
        {
            var entitlements = Entitlements(c, true);
            var planRows = Reconcile(c, true, new RecordingNotifier());
            var slots = new PlanSlotService(c, entitlements, new Be2.FixedPlanCatalog(OwnerPlanCatalog.Seeded()),
                new Be2.FixedPaymentFeature(PaymentFeatureStates.On()), planRows);
            var result = await StayHandlers.DeleteHiddenRoom(hidden.Id, Principal(owner.UserId), new UnitOfWork(c), c, slots, entitlements, planRows,
                new StayHubReader(c, entitlements, TimeProvider.System), NoServices());
            Assert.Equal(400, StatusOf(result));
        }
        Assert.Equal(3, (await RoomsOfAsync(owner.ListingId)).Count);
        Assert.DoesNotContain(await RoomsOfAsync(owner.ListingId), r => r.HiddenByPlan);
        Assert.Equal("This room is active again.", StayHandlers.RoomActiveAgain);
    }

    [IntegrationFact]
    public async Task Jobs_two_posts_at_the_free_limit_at_once_give_one_job_and_one_409()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);
        var seed = (await SeedJobsAsync(owner, 1))[0];
        await using (var c = CreateContext())
            await c.StayJobs.Where(j => j.Id == seed.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, StayJobStatuses.Closed));

        async Task<int> PostAsync()
        {
            await using var c = CreateContext();
            var request = new UpsertStayJobRequest
            {
                Title = "Cook", Description = "Cook for the stay kitchen.", JobDepartmentId = seed.JobDepartmentId, JobRoleId = seed.JobRoleId,
                JobTypeId = seed.JobTypeId, JobShiftTypeId = seed.JobShiftTypeId, JobSalaryTypeId = seed.JobSalaryTypeId,
                MinimumExperienceYears = 1, MinSalary = 15000, MaxSalary = 20000, ContactPhone = "9876543210", WhatsAppNumber = "9876543210",
            };
            return StatusOf(await StayJobHandlers.Create(request, new UpsertStayJobRequestValidator(), Principal(owner.UserId), new UnitOfWork(c), c,
                new RecordingPublisher(), Entitlements(c, true), NullLoggerFactory.Instance, new InMemoryRateLimitService(), new DefaultHttpContext(),
                new OutboxWriter(c)));
        }

        var codes = await Task.WhenAll(PostAsync(), PostAsync());
        Assert.Single(codes, s => s == 409);
        Assert.Single(codes, s => s is >= 200 and < 300);
        Assert.Equal(1, (await JobsOfAsync(owner.UserId)).Count(j => j.Status == StayJobStatuses.Active));
    }
}

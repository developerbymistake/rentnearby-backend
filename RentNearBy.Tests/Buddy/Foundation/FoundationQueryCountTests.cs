using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Foundation;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. The seams every lane builds on: fixed statement counts.
public class FoundationQueryCountTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Guid _trip;
    private Guid _owner;
    private List<Guid> _order = [];

    private async Task SeedTripAsync(ApplicationDbContext ctx, int rows)
    {
        var people = await BuddySeed.UsersAsync(ctx, Math.Min(rows, 11) + 1);
        _owner = people[0];
        _trip = await BuddySeed.TripAsync(ctx, _owner);
        await BuddySeed.MembersAsync(ctx, _trip, _owner, people.Skip(1));
        await BuddySeed.ExpensesAsync(ctx, _trip, _owner, rows, people);
        await BuddySeed.PaymentAsync(ctx, _trip, people[1], _owner, 10, PaymentStates.Received);
        _order = people;
    }

    [IntegrationFact]
    public Task TripAccess_Get_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedTripAsync,
        async ctx => Assert.NotNull(await new TripAccess(ctx).GetAsync(_trip, _owner)), budget: 1);

    [IntegrationFact]
    public Task TripAccess_Travellers_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedTripAsync,
        async ctx => Assert.Equal(_order.Count, (await new TripAccess(ctx).GetTravellersAsync(_trip, includeFormer: true)).Count), budget: 1);

    [IntegrationFact]
    public Task TripLedger_Load_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedTripAsync,
        async ctx => Assert.NotEmpty((await new TripLedger(ctx, NullLogger<TripLedger>.Instance).LoadAsync(_trip, _order)).People), budget: 3);

    [IntegrationFact]
    public async Task TripLedger_Refresh_writes_the_state_row_and_bumps_the_version()
    {
        await using (var ctx = CreateContext()) await SeedTripAsync(ctx, 3);
        await using var counting = Db.CreateCountingContext(out var counter);
        var ledger = new TripLedger(counting, NullLogger<TripLedger>.Instance);
        var first = await ledger.RefreshStateAsync(_trip, _order, null);
        var second = await ledger.RefreshStateAsync(_trip, _order, Guid.NewGuid());
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(3, second.ExpenseCount);
        Assert.Equal(3000, second.TotalSpent);
        Assert.Equal(6, counter.Count);
        Assert.NotNull(second.ReopenedAt);
        await using var check = CreateContext();
        Assert.Equal(2, check.Set<TripExpenseState>().Single(s => s.TripId == _trip).Version);
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TripExecution;

[Collection(IntegrationCollection.Name)]
public class TripExecutionSeedTests(TestDatabase db)
{
    [IntegrationFact]
    public async Task Seeder_adds_the_trip_row_off_once_and_leaves_the_payment_row()
    {
        await using var ctx = db.CreateContext();
        var trip = await ctx.AppFeatureFlags.FirstOrDefaultAsync(f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled);
        if (trip != null)
        {
            ctx.AppFeatureFlags.Remove(trip);
            await ctx.SaveChangesAsync();
        }
        var paymentBefore = await ctx.AppFeatureFlags.AsNoTracking()
            .Where(f => f.FeatureKey == AppFeatureKeys.PaymentEnabled).Select(f => f.IsEnabled).ToListAsync();

        await DataSeeder.SeedAppFeatureFlagsAsync(ctx);
        await DataSeeder.SeedAppFeatureFlagsAsync(ctx);

        var rows = await ctx.AppFeatureFlags.AsNoTracking().ToListAsync();
        var tripRow = Assert.Single(rows, f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled);
        Assert.False(tripRow.IsEnabled);
        var payment = Assert.Single(rows, f => f.FeatureKey == AppFeatureKeys.PaymentEnabled);
        if (paymentBefore.Count == 1) Assert.Equal(paymentBefore[0], payment.IsEnabled);
    }
}

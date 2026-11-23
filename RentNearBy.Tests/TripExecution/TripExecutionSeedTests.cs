using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TripExecution;

// Change 24 (owner 2026-11-24): payment, trip bookings and places on maps all seed ON; stored rows are never overwritten.
[Collection(IntegrationCollection.Name)]
public class TripExecutionSeedTests(TestDatabase db)
{
    private static readonly string[] Keys =
        [AppFeatureKeys.PaymentEnabled, AppFeatureKeys.TripExecutionEnabled, AppFeatureKeys.MapDiscoveryEnabled];

    [IntegrationFact]
    public async Task Seeder_adds_every_switch_on_once()
    {
        await using var ctx = db.CreateContext();
        var before = await ctx.AppFeatureFlags.AsNoTracking().Where(f => Keys.Contains(f.FeatureKey)).ToListAsync();
        try
        {
            await ctx.AppFeatureFlags.Where(f => Keys.Contains(f.FeatureKey)).ExecuteDeleteAsync();

            await DataSeeder.SeedAppFeatureFlagsAsync(ctx);
            await DataSeeder.SeedAppFeatureFlagsAsync(ctx);

            var rows = await ctx.AppFeatureFlags.AsNoTracking().ToListAsync();
            foreach (var key in Keys) Assert.True(Assert.Single(rows, f => f.FeatureKey == key).IsEnabled, key);
            var payment = rows.Single(f => f.FeatureKey == AppFeatureKeys.PaymentEnabled);
            Assert.Equal(30, payment.FreeDurationDays);
            Assert.Equal(PaymentProviders.Play, payment.PaymentProvider);
        }
        finally
        {
            await ctx.AppFeatureFlags.Where(f => Keys.Contains(f.FeatureKey)).ExecuteDeleteAsync();
            ctx.ChangeTracker.Clear();
            ctx.AppFeatureFlags.AddRange(before);
            await ctx.SaveChangesAsync();
        }
    }

    [IntegrationFact]
    public async Task Seeder_leaves_a_stored_off_switch_off()
    {
        await using var ctx = db.CreateContext();
        var trip = await ctx.AppFeatureFlags.FirstOrDefaultAsync(f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled);
        if (trip is null)
        {
            await DataSeeder.SeedAppFeatureFlagsAsync(ctx);
            trip = await ctx.AppFeatureFlags.SingleAsync(f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled);
        }
        var was = trip.IsEnabled;
        try
        {
            trip.IsEnabled = false;
            await ctx.SaveChangesAsync();

            await DataSeeder.SeedAppFeatureFlagsAsync(ctx);

            var rows = await ctx.AppFeatureFlags.AsNoTracking().Where(f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled).ToListAsync();
            Assert.False(Assert.Single(rows).IsEnabled);
        }
        finally
        {
            trip.IsEnabled = was;
            await ctx.SaveChangesAsync();
        }
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Social;

// Spec 23.2 (2026-11-27): Bakhli Plus = 10 new chats a month; a seeded Plus row still at the old 15 moves to 10, an admin value stays.
[Collection(IntegrationCollection.Name)]
public class SeekerPlusSeedSyncTests(TestDatabase db)
{
    [IntegrationFact]
    public async Task Old_seed_value_moves_to_ten_and_an_admin_value_is_kept()
    {
        await using var ctx = db.CreateContext();
        await DataSeeder.SeedSeekerPlansAsync(ctx);
        var row = await ctx.PlanFeatures.SingleAsync(f => f.PlanId == SeekerPlanSeed.PlusPlanId && f.Key == SeekerPlanKeys.NewChatsPerMonth);
        var was = row.Value;
        try
        {
            row.Value = DataSeeder.OldSeekerPlusNewChatsSeed;
            await ctx.SaveChangesAsync();
            await DataSeeder.SyncSeekerPlusNewChatsAsync(ctx);
            Assert.Equal(10, (await ctx.PlanFeatures.AsNoTracking().SingleAsync(f => f.Id == row.Id)).Value);

            row.Value = 12;
            await ctx.SaveChangesAsync();
            await DataSeeder.SyncSeekerPlusNewChatsAsync(ctx);
            Assert.Equal(12, (await ctx.PlanFeatures.AsNoTracking().SingleAsync(f => f.Id == row.Id)).Value);
        }
        finally
        {
            row.Value = was;
            await ctx.SaveChangesAsync();
        }
    }
}

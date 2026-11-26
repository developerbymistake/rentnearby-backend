using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Regression;

// Owner decision 2026-11-27 (live in Play Console): Stay Plus 500/1400/4800, Taxi Plus 300/800/2800, Bakhli Plus 200/500/1800,
// no free trial for any role (Play offers trial-7d inactive). The seeder must carry these values and no trial.
public class NoTrialSeedGuardTests
{
    private static string Seeder()
        => File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Data", "DataSeeder.cs"));

    [Fact]
    public void Owner_plus_prices_are_the_play_console_prices()
    {
        var source = Seeder();
        Assert.Contains("ListingKinds.Stay => (500, 1400, 4800)", source);
        Assert.Contains("ListingKinds.Taxi => (300, 800, 2800)", source);
    }

    [Fact]
    public void Seeker_plus_prices_are_the_play_console_prices()
    {
        Assert.Equal(200, SeekerPlanSeed.Price1m);
        Assert.Equal(500, SeekerPlanSeed.Price3m);
        Assert.Equal(1800, SeekerPlanSeed.Price12m);
    }

    [Fact]
    public void No_plan_is_seeded_with_a_trial()
    {
        var source = Seeder();
        Assert.DoesNotMatch(@"TrialDays\s*=\s*[1-9]", source);
        Assert.DoesNotMatch(@"PlayTrialOfferId\s*=\s*""", source);
        Assert.Contains("SyncSeedNoTrialAsync(db)", source);
        Assert.DoesNotContain("trial-7d", source);
    }

    [Fact]
    public void Old_seed_prices_move_to_the_new_prices_on_an_existing_database()
    {
        var source = Seeder();
        foreach (var row in new[]
                 {
                     "(3, 1, 400, 500)", "(3, 3, 1100, 1400)", "(3, 12, 3800, 4800)",
                     "(4, 1, 150, 300)", "(4, 3, 400, 800)", "(4, 12, 1400, 2800)",
                 })
            Assert.Contains(row, source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}

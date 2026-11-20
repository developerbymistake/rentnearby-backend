using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Support.Buddy;

public static class QueryAssert
{
    // Seeds `small` rows, runs act through a counting context; seeds `large` rows, runs act again. Both counts must be
    // equal and within budget. seed gets a plain (uncounted) context and the row count; it sets the ids act uses.
    public static async Task SameCountAsync(
        TestDatabase db, Func<ApplicationDbContext, int, Task> seed, Func<ApplicationDbContext, Task> act,
        int budget, int small = 3, int large = 30)
    {
        var smallRun = await RunAsync(db, seed, act, small);
        var largeRun = await RunAsync(db, seed, act, large);
        AssertRuns(smallRun, largeRun, budget, small, large);
    }

    public static void AssertRuns(IReadOnlyList<string> smallRun, IReadOnlyList<string> largeRun, int budget, int small, int large)
    {
        var detail = "Statements with " + large + " rows:\n" + string.Join("\n---\n", largeRun);
        Assert.True(smallRun.Count == largeRun.Count,
            $"Query count depends on row count: {smallRun.Count} with {small} rows, {largeRun.Count} with {large} rows.\n{detail}");
        Assert.True(largeRun.Count <= budget, $"Query count {largeRun.Count} is over the budget {budget}.\n{detail}");
    }

    private static async Task<IReadOnlyList<string>> RunAsync(
        TestDatabase db, Func<ApplicationDbContext, int, Task> seed, Func<ApplicationDbContext, Task> act, int rows)
    {
        await using (var plain = db.CreateContext())
            await seed(plain, rows);
        await using var counting = db.CreateCountingContext(out var counter);
        counter.Reset();
        await act(counting);
        return counter.Commands;
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Data;

public abstract class DatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public ApplicationDbContext CreateContext()
    {
        if (!IsEnabled)
            throw new InvalidOperationException($"No test database configured. {TestDatabase.SkipReason}.");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString!, o => o.UseNetTopologySuite())
            .Options;
        return new ApplicationDbContext(options);
    }

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await StartAsync();
        if (IsEnabled) await PrepareAsync();
    }

    // Returns the connection string of the database for this run, or null when none is available.
    protected abstract Task<string?> StartAsync();

    // Override to migrate or reset the database once per test run.
    protected virtual Task PrepareAsync() => Task.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

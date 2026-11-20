using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Support.Buddy;

// Counts SQL statements sent through EF (reader, scalar, non-query; sync and async). Transaction control, SAVEPOINT,
// "SET " and advisory locks are not counted; the trip row lock is.
public sealed class QueryCounter : DbCommandInterceptor
{
    private readonly List<string> _commands = [];
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _commands.Count; }
    }

    public IReadOnlyList<string> Commands
    {
        get { lock (_gate) return _commands.ToList(); }
    }

    public void Reset()
    {
        lock (_gate) _commands.Clear();
    }

    public static bool IsCounted(string sql)
    {
        var text = sql.TrimStart();
        if (text.Length == 0) return false;
        string[] skipped = ["BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "START TRANSACTION", "SET "];
        if (skipped.Any(s => text.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        return !text.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase);
    }

    private void Record(DbCommand command)
    {
        if (!IsCounted(command.CommandText)) return;
        lock (_gate) _commands.Add(command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}

public static class QueryCounterExtensions
{
    public static DbContextOptions<ApplicationDbContext> CountingOptions(string connectionString, QueryCounter counter)
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, o => o.UseNetTopologySuite())
            .AddInterceptors(counter)
            .Options;

    public static ApplicationDbContext CreateCountingContext(this TestDatabase db, out QueryCounter counter)
    {
        if (!db.IsEnabled) throw new InvalidOperationException($"No test database configured. {TestDatabase.SkipReason}.");
        counter = new QueryCounter();
        return new ApplicationDbContext(CountingOptions(db.ConnectionString!, counter));
    }
}

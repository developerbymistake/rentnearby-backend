using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// One retry of a whole unit of work on a deadlock (40P01) or serialization failure (40001), MASTER_PLAN 2.4.
// The unit must open and dispose its own transaction, so the retry starts from a clean connection state.
public static class PgRetry
{
    public const string DeadlockDetected = "40P01", SerializationFailure = "40001";

    public static bool IsRetryable(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException pg && (pg.SqlState == DeadlockDetected || pg.SqlState == SerializationFailure))
                return true;
        return false;
    }

    public static async Task<T> OnceAsync<T>(Func<CancellationToken, Task<T>> unit, CancellationToken ct, ILogger? logger = null, Action? beforeRetry = null)
    {
        try
        {
            return await unit(ct);
        }
        catch (Exception ex) when (IsRetryable(ex) && !ct.IsCancellationRequested)
        {
            logger?.LogWarning("Taxi request unit hit {SqlState}, retrying once", SqlStateOf(ex));
            beforeRetry?.Invoke();
            return await unit(ct);
        }
    }

    public static Task OnceAsync(Func<CancellationToken, Task> unit, CancellationToken ct, ILogger? logger = null, Action? beforeRetry = null)
        => OnceAsync<bool>(async c => { await unit(c); return true; }, ct, logger, beforeRetry);

    // Entities tracked by the failed attempt are dropped before the retry so it does not insert them twice.
    public static Task<T> OnceAsync<T>(ApplicationDbContext db, Func<CancellationToken, Task<T>> unit, CancellationToken ct, ILogger? logger = null)
        => OnceAsync(unit, ct, logger, db.ChangeTracker.Clear);

    public static Task OnceAsync(ApplicationDbContext db, Func<CancellationToken, Task> unit, CancellationToken ct, ILogger? logger = null)
        => OnceAsync(unit, ct, logger, db.ChangeTracker.Clear);

    private static string? SqlStateOf(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e is PostgresException pg) return pg.SqlState;
        return null;
    }
}

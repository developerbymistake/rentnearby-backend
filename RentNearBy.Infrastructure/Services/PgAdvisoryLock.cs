using Npgsql;

namespace RentNearBy.Infrastructure.Services;

// Session-level advisory lock on a dedicated connection held for the whole run; released explicitly, then the connection closes.
// Correctness never depends on it (RemindedOn and the unique outbox Key stay the guards); it only stops two instances duplicating work.
public sealed class PgAdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _name;

    private PgAdvisoryLock(NpgsqlConnection connection, string name)
    {
        _connection = connection;
        _name = name;
    }

    public static async Task<PgAdvisoryLock?> TryAcquireAsync(string connectionString, string name, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@name))", connection);
            cmd.Parameters.AddWithValue("name", name);
            if (await cmd.ExecuteScalarAsync(ct) is true) return new PgAdvisoryLock(connection, name);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(@name))", _connection);
            cmd.Parameters.AddWithValue("name", _name);
            await cmd.ExecuteScalarAsync();
        }
        catch
        {
        }
        await _connection.DisposeAsync();
    }
}

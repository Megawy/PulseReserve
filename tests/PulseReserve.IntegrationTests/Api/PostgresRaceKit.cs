using Npgsql;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>
/// A way to make a database race observable instead of hoped for. A separate connection takes a row lock and keeps its
/// transaction open; the callers are then started one at a time, and each is not considered "in the race" until PostgreSQL
/// itself reports one more session waiting on a lock (<c>pg_stat_activity.wait_event_type = 'Lock'</c>). Only when every
/// caller is provably contending is the lock released.
/// <para>
/// What this controls: that all callers are in flight and queued behind the same row when the lock is released, with a bounded
/// wait. What it does NOT control: which waiter PostgreSQL wakes first, so a test using it must assert invariants that hold
/// for every order (and say so), and it must not claim a specific interleaving. A caller that never blocks (for example one that
/// uses <c>SKIP LOCKED</c>) makes the wait time out, which fails the test loudly instead of passing without contention.
/// These helpers need a real PostgreSQL (Docker) and have not been executed in this working tree.
/// </para>
/// </summary>
internal sealed class RowLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private NpgsqlTransaction? _transaction;

    private RowLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>Opens a connection, starts a transaction and runs <paramref name="lockingSql"/> (for example a SELECT ... FOR UPDATE).</summary>
    public static async Task<RowLock> AcquireAsync(string connectionString, string lockingSql)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(lockingSql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        return new RowLock(connection, transaction);
    }

    /// <summary>Runs more SQL inside the lock-holding transaction (to simulate an operation that is still in flight).</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ReleaseAsync()
    {
        if (_transaction is null)
            return;
        await _transaction.CommitAsync();
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await _connection.DisposeAsync();
    }
}

internal static class PostgresRace
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Waits until PostgreSQL reports at least <paramref name="expected"/> sessions of this database blocked on a lock.</summary>
    public static async Task WaitForBlockedSessionsAsync(string connectionString, int expected)
    {
        var deadline = DateTime.UtcNow + Timeout;
        await using var monitor = new NpgsqlConnection(connectionString);
        await monitor.OpenAsync();
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", monitor);
            var blocked = (long)(await command.ExecuteScalarAsync())!;
            if (blocked >= expected)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {expected} session(s) blocked on a lock but PostgreSQL reports {blocked}: a caller did not contend for the locked row.");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Holds <paramref name="lockingSql"/>'s locks, starts the callers one by one (each only after the previous one is
    /// observed waiting), then releases the lock and returns every result. Throws on timeout instead of hanging.
    /// </summary>
    public static async Task<T[]> RaceBehindLockAsync<T>(string connectionString, string lockingSql, params Func<Task<T>>[] callers)
    {
        await using var held = await RowLock.AcquireAsync(connectionString, lockingSql);
        var running = new List<Task<T>>();
        for (var i = 0; i < callers.Length; i++)
        {
            var caller = callers[i];
            running.Add(Task.Run(caller));
            await WaitForBlockedSessionsAsync(connectionString, i + 1);
        }

        await held.ReleaseAsync();
        return await Task.WhenAll(running).WaitAsync(Timeout);
    }
}

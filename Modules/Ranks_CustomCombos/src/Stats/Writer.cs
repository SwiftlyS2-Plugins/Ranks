using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Stats;

/// <summary>Writes combo counts additively, off-thread.</summary>
/// <remarks>
/// Additive rather than absolute for the same reason as <c>Ranks_WeaponStats</c>: this module
/// holds only the counts since the last flush, so two servers sharing a database accumulate
/// instead of clobbering, and nothing has to be read back on connect.
///
/// Every change runs through one queue, one at a time and in call order. A reset or purge queued
/// on the game thread therefore lands after every flush cut before it, so a write already under
/// way cannot bring back what it removed (FR-DB-10).
/// </remarks>
internal sealed class Writer
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Func<bool> _detailedLogging;
    private readonly Lock _queueGate = new();

    /// <summary>Changes a failed transaction left behind, applied again in queue order ahead of the next ones.</summary>
    /// <remarks>Touched only by queued operations, which never overlap, so it needs no lock of its own.</remarks>
    private readonly List<Change> _retry = [];

    /// <summary>How many failures of its own statements drop a reset or purge from the retry buffer.</summary>
    /// <remarks>
    /// Unbounded, one the database always refuses — no DELETE permission, a period table never created — holds every
    /// later write back for the rest of the load. Only its own statements count, unless the driver marks the failure transient.
    /// </remarks>
    private const int ClearAttempts = 3;

    private Task _queue = Task.CompletedTask;
    private string _connectionName = "default";
    private string _table = "ranks_combos";
    private string _periodTable = "ranks_combos_period";
    private bool _mysql;

    public Writer(ISwiftlyCore core, ILogger logger, Func<bool> detailedLogging)
    {
        _core = core;
        _logger = logger;
        _detailedLogging = detailedLogging;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the writer refuses to write.</summary>
    public void Configure(string connectionName, string table, string periodTable)
    {
        _connectionName = connectionName;
        _table = table;
        _periodTable = periodTable;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    public Task FlushAsync(List<ComboDelta> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        return Enqueue(() => Commit(batch.ConvertAll<Change>(delta => new Change.Add(delta))));
    }

    /// <summary>Clears a reset player's counts: all of them while <paramref name="period"/> is null, else that period's.</summary>
    /// <remarks>
    /// With periods on, the period's counts also come off the lifetime totals, so the past periods
    /// core keeps still add up to them (FR-DB-10).
    /// </remarks>
    public Task ResetAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => Reset(steamId, period));
    }

    /// <summary>Deletes purged players' rows from both tables.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — its tables are keyed on
    /// SteamID64 with nothing joining them back to core, so nothing else sweeps them (FR-DB-08).
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToHashSet();

        return Enqueue(() => Purge(ids));
    }

    /// <summary>Deletes rows, in both tables, for players core no longer knows about.</summary>
    /// <remarks>
    /// PlayersPurged is a live event, so a purge that happens while this module is unloaded is
    /// never swept and those rows grow without bound. Safe to depend on core's table here:
    /// FR-DB-04 fixes ranks_players as a closed, known schema, which makes it a contract rather
    /// than an implementation detail.
    /// </remarks>
    public Task ReconcileOrphansAsync(string playersTable)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => ReconcileOrphans(playersTable));
    }

    /// <summary>Every combo a player has landed, highest first.</summary>
    public Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadAsync(ulong steamId) =>
        ReadRowsAsync(steamId, null);

    /// <summary>Every combo a player landed in one period, highest first.</summary>
    public Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadAsync(ulong steamId, long period) =>
        ReadRowsAsync(steamId, period);

    /// <remarks>Reads the lifetime table when <paramref name="period"/> is null.</remarks>
    private Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadRowsAsync(ulong steamId, long? period) =>
        Task.Run(() =>
        {
            var rows = new List<(string, long, DateTime)>();

            if (!IsReady) return rows;

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = period is null
                ? $"SELECT combo_key, count, last_at FROM {_table} WHERE steam_id = @steam_id ORDER BY count DESC"
                : $"SELECT combo_key, count, last_at FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period ORDER BY count DESC";
            Add(command, "@steam_id", (long)steamId);

            if (period is { } value) Add(command, "@period", value);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2)));

            return rows;
        });

    /// <summary>Runs <paramref name="operation"/> off-thread once everything queued before it has finished.</summary>
    /// <remarks>
    /// Continues past a failed predecessor: every operation catches and logs its own failure, so
    /// the queue itself never stalls.
    /// </remarks>
    private Task Enqueue(Action operation)
    {
        lock (_queueGate)
        {
            _queue = _queue.ContinueWith(
                _ => operation(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);

            return _queue;
        }
    }

    /// <summary>Applies whatever a failed transaction left behind, then <paramref name="changes"/>, in queue order and in one transaction.</summary>
    /// <remarks>
    /// Never reordered: a reset or purge that failed stays ahead of every count drained after it, so those land on the
    /// cleared rows instead of being cleared with them, until <see cref="GiveUpOn"/> drops it. A failed transaction rolled
    /// every change back, so all of them stay. Logged ungated: it is the data-loss path.
    /// </remarks>
    private void Commit(IEnumerable<Change> changes)
    {
        _retry.AddRange(changes);

        if (_retry.Count == 0) return;

        Change? applying = null;

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var change in _retry)
            {
                applying = change;
                Apply(connection, transaction, change);
            }

            applying = null;
            transaction.Commit();
            _retry.Clear();
        }
        catch (Exception ex)
        {
            if (applying is { } failed && GiveUpOn(failed, ex)) Commit([]);
            else _logger.LogError(ex, "Ranks_CustomCombos: writing {Count} change(s) failed; they are buffered for retry.", _retry.Count);
        }
    }

    /// <summary>
    /// Counts a failure of a reset's or purge's own statements that the driver does not mark transient, and drops the
    /// change at <see cref="ClearAttempts"/> of them.
    /// </summary>
    /// <returns>True when it was dropped, so the changes queued behind it are written at once.</returns>
    /// <remarks>A count is never given up on: it is kept until it is written.</remarks>
    private bool GiveUpOn(Change failed, Exception error)
    {
        if (failed is Change.Add || IsTransient(error) || ++failed.Failures < ClearAttempts) return false;

        _retry.RemoveAll(change => ReferenceEquals(change, failed));
        ReportDropped(failed, error);

        return true;
    }

    /// <summary>Whether the driver marks <paramref name="error"/>, or an exception it wraps, as transient: worth retrying unchanged.</summary>
    /// <remarks>
    /// Npgsql marks lost connections, deadlocks, and lock and command timeouts; MySqlConnector deadlocks and InnoDB
    /// lock-wait timeouts but no lost connection or command timeout; System.Data.SQLite nothing, so every failure counts.
    /// </remarks>
    private static bool IsTransient(Exception? error) =>
        error is not null && (error is DbException { IsTransient: true } || IsTransient(error.InnerException));

    /// <remarks>Ungated: what the change would have cleared now stays.</remarks>
    private void ReportDropped(Change dropped, Exception error)
    {
        switch (dropped)
        {
            case Change.Reset reset:
                _logger.LogError(
                    error,
                    "Ranks_CustomCombos: gave up resetting the combo counts of {SteamId} after {Attempts} failed " +
                    "attempts; they stay as they were.",
                    reset.SteamId, dropped.Failures);
                break;

            case Change.Purge purge:
                _logger.LogError(
                    error,
                    "Ranks_CustomCombos: gave up deleting the combo counts of {Count} purged player(s) after " +
                    "{Attempts} failed attempts; their rows stay.",
                    purge.SteamIds.Count, dropped.Failures);
                break;
        }
    }

    private void Apply(IDbConnection connection, IDbTransaction transaction, Change change)
    {
        switch (change)
        {
            case Change.Add add:
                AddDelta(connection, transaction, add.Delta);
                break;

            case Change.Reset { Period: { } current } reset:
                ResetPeriod(connection, transaction, reset.SteamId, current);
                break;

            case Change.Reset reset:
                Execute(connection, transaction, $"DELETE FROM {_table} WHERE steam_id = @steam_id", reset.SteamId);
                break;

            case Change.Purge purge:
                Purge(connection, transaction, purge.SteamIds);
                break;
        }
    }

    /// <summary>Adds one delta to the lifetime table, and to its period's row when it carries one.</summary>
    private void AddDelta(IDbConnection connection, IDbTransaction transaction, ComboDelta delta)
    {
        WriteOne(connection, transaction, delta);

        if (delta.Period is { } period)
            WritePeriod(connection, transaction, delta, period);
    }

    private void WriteOne(IDbConnection connection, IDbTransaction transaction, ComboDelta delta)
    {
        const string Columns = "steam_id, combo_key, count, last_at";
        const string Values = "@steam_id, @combo_key, @count, @last_at";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // VALUES() rather than MySQL 8.0.20's "AS new" row alias: MariaDB does not support the
        // alias at all, and VALUES() works on both.
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_table} ({Columns}) VALUES ({Values})
               ON DUPLICATE KEY UPDATE count = count + VALUES(count), last_at = VALUES(last_at)
               """
            // Qualified with the table name, or the reference is ambiguous against EXCLUDED.
            : $"""
               INSERT INTO {_table} ({Columns}) VALUES ({Values})
               ON CONFLICT (steam_id, combo_key) DO UPDATE SET
                   count = {_table}.count + EXCLUDED.count,
                   last_at = EXCLUDED.last_at
               """;

        Add(command, "@steam_id", (long)delta.SteamId);
        Add(command, "@combo_key", delta.ComboKey);
        Add(command, "@count", delta.Count);
        Add(command, "@last_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <summary><see cref="WriteOne"/>'s upsert against the period table, keyed on the period as well.</summary>
    private void WritePeriod(IDbConnection connection, IDbTransaction transaction, ComboDelta delta, long period)
    {
        const string Columns = "steam_id, combo_key, period, count, last_at";
        const string Values = "@steam_id, @combo_key, @period, @count, @last_at";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_periodTable} ({Columns}) VALUES ({Values})
               ON DUPLICATE KEY UPDATE count = count + VALUES(count), last_at = VALUES(last_at)
               """
            : $"""
               INSERT INTO {_periodTable} ({Columns}) VALUES ({Values})
               ON CONFLICT (steam_id, combo_key, period) DO UPDATE SET
                   count = {_periodTable}.count + EXCLUDED.count,
                   last_at = EXCLUDED.last_at
               """;

        Add(command, "@steam_id", (long)delta.SteamId);
        Add(command, "@combo_key", delta.ComboKey);
        Add(command, "@period", period);
        Add(command, "@count", delta.Count);
        Add(command, "@last_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Retry entries the reset covers are dropped first, or they would be written straight back; every flush cut
    /// before the reset has already run, so all of them predate it. A clear that fails stays in the buffer ahead of
    /// every count drained after it, so a retry takes away only what the player held before the reset, never what
    /// they landed since.
    /// </remarks>
    private void Reset(ulong steamId, long? period)
    {
        _retry.RemoveAll(change =>
            change is Change.Add { Delta: var delta } && delta.SteamId == steamId && (period is null || delta.Period == period));

        Commit([new Change.Reset(steamId, period)]);
    }

    /// <summary>Takes one period's counts off the lifetime totals, then deletes that period's rows.</summary>
    /// <remarks>
    /// Clamped at zero: a reset made while periods were off cleared the lifetime rows but not the
    /// period rows, so a lifetime count can be the smaller. A lifetime row brought to zero is deleted,
    /// because the player has then not landed that combo outside the period just cleared.
    /// </remarks>
    private void ResetPeriod(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        foreach (var (comboKey, count) in ReadPeriodCounts(connection, transaction, steamId, period))
            Subtract(connection, transaction, steamId, comboKey, count);

        Execute(connection, transaction, $"DELETE FROM {_table} WHERE steam_id = @steam_id AND count <= 0", steamId);
        Execute(connection, transaction, $"DELETE FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period", steamId, period);
    }

    /// <remarks>Read to the end before anything else runs: MySqlConnector allows one open reader per connection.</remarks>
    private List<(string ComboKey, long Count)> ReadPeriodCounts(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT combo_key, count FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)steamId);
        Add(command, "@period", period);

        var rows = new List<(string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));

        return rows;
    }

    private void Subtract(IDbConnection connection, IDbTransaction transaction, ulong steamId, string comboKey, long count)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {_table} SET count = CASE WHEN count > @count THEN count - @count ELSE 0 END WHERE steam_id = @steam_id AND combo_key = @combo_key";
        Add(command, "@count", count);
        Add(command, "@steam_id", (long)steamId);
        Add(command, "@combo_key", comboKey);

        command.ExecuteNonQuery();
    }

    /// <remarks>Retry entries for these players go first, or a retried write would recreate their rows.</remarks>
    private void Purge(HashSet<ulong> steamIds)
    {
        _retry.RemoveAll(change => change is Change.Add { Delta: var delta } && steamIds.Contains(delta.SteamId));

        Commit([new Change.Purge(steamIds)]);
    }

    /// <summary>Deletes every row of the purged players from both tables.</summary>
    private void Purge(IDbConnection connection, IDbTransaction transaction, HashSet<ulong> steamIds)
    {
        foreach (var table in (ReadOnlySpan<string>)[_table, _periodTable])
        {
            var sql = $"DELETE FROM {table} WHERE steam_id = @steam_id";

            foreach (var steamId in steamIds)
                Execute(connection, transaction, sql, steamId);
        }
    }

    /// <remarks>What it removed is expected maintenance, so the count is gated; a failure is not.</remarks>
    private void ReconcileOrphans(string playersTable)
    {
        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            var removed = 0;

            foreach (var table in (ReadOnlySpan<string>)[_table, _periodTable])
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE steam_id NOT IN (SELECT steam_id FROM {playersTable})";
                removed += command.ExecuteNonQuery();
            }

            if (removed > 0 && _detailedLogging())
                _logger.LogInformation("Ranks_CustomCombos: removed {Count} orphaned row(s) left by a purge this module missed.", removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_CustomCombos: orphan reconcile failed.");
        }
    }

    /// <summary>Runs one statement keyed on <c>@steam_id</c>, and on <c>@period</c> when one is given.</summary>
    private static void Execute(IDbConnection connection, IDbTransaction transaction, string sql, ulong steamId, long? period = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "@steam_id", (long)steamId);

        if (period is { } value) Add(command, "@period", value);

        command.ExecuteNonQuery();
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>One queued change to this module's rows: a drained count, a core reset, or a purge.</summary>
    /// <remarks>The retry buffer holds these in queue order, so a failed reset or purge is retried where it was queued.</remarks>
    private abstract record Change
    {
        /// <summary>How many times this change's own statements have failed, transient failures aside; counted for a reset or a purge only.</summary>
        public int Failures { get; set; }

        /// <summary>A count added to the lifetime row, and to its period's row when it carries one.</summary>
        public sealed record Add(ComboDelta Delta) : Change;

        /// <summary>A core reset: one period's counts cleared and taken off the lifetime totals, or every lifetime row while periods are off.</summary>
        public sealed record Reset(ulong SteamId, long? Period) : Change;

        /// <summary>Purged players, deleted from both tables.</summary>
        public sealed record Purge(HashSet<ulong> SteamIds) : Change;
    }
}

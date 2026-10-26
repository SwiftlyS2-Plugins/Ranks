using System.Data;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Stats;

/// <summary>Writes combo counts additively, off-thread.</summary>
/// <remarks>
/// Additive rather than absolute for the same reason as <c>Ranks_WeaponStats</c>: this module
/// holds only the counts since the last flush, so two servers sharing a database accumulate
/// instead of clobbering, and nothing has to be read back on connect.
/// </remarks>
internal sealed class Writer
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _retryGate = new();
    private readonly List<ComboDelta> _retry = [];

    private string _connectionName = "default";
    private string _table = "ranks_combos";
    private bool _mysql;

    public Writer(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the writer refuses to write.</summary>
    public void Configure(string connectionName, string table)
    {
        _connectionName = connectionName;
        _table = table;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    public Task FlushAsync(List<ComboDelta> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            List<ComboDelta> pending;

            lock (_retryGate)
            {
                _retry.AddRange(batch);
                pending = [.. _retry];
                _retry.Clear();
            }

            try
            {
                Write(pending);
            }
            catch (Exception ex)
            {
                lock (_retryGate) _retry.AddRange(pending);

                // Data-loss path, so never gated behind DetailedLogging.
                _logger.LogError(ex, "Ranks_CustomCombos: flush of {Count} row(s) failed; they are buffered for retry.", pending.Count);
            }
        });
    }

    /// <summary>Every combo a player has landed, highest first.</summary>
    public Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadAsync(ulong steamId) =>
        Task.Run(() =>
        {
            var rows = new List<(string, long, DateTime)>();

            if (!IsReady) return rows;

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT combo_key, count, last_at FROM {_table} WHERE steam_id = @steam_id ORDER BY count DESC";
            Add(command, "@steam_id", (long)steamId);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2)));

            return rows;
        });

    private void Write(List<ComboDelta> batch)
    {
        using var connection = _core.Database.GetConnection(_connectionName);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var delta in batch)
            WriteOne(connection, transaction, delta);

        transaction.Commit();
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

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

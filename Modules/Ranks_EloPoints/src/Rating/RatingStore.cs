using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Match;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Rating;

/// <summary>Reads and writes the authoritative rating. Off-thread, never touches a live session.</summary>
/// <remarks>
/// The upsert replaces <c>rating</c> rather than accumulating it, the opposite of
/// Ranks_WeaponStats: that module holds session deltas with no read on connect, while this one
/// reads the current value, computes a new one and writes it.
/// </remarks>
internal sealed class RatingStore
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _retryGate = new();
    private readonly List<RatingWrite> _retry = [];

    /// <summary>Last known rating per player, so a resync can run without a read.</summary>
    private readonly ConcurrentDictionary<ulong, double> _cache = new();

    private string _connectionName = "default";
    private string _table = "ranks_elopoints";
    private bool _mysql;

    public RatingStore(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the store refuses to touch the database.</summary>
    public void Configure(string connectionName, string table)
    {
        _connectionName = connectionName;
        _table = table;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    public bool TryGetCached(ulong steamId, out double rating) => _cache.TryGetValue(steamId, out rating);

    /// <summary>Reads a stored rating, or null when the player has none yet.</summary>
    /// <remarks>
    /// Off the game thread: a blocking database read at a round boundary is a stall. A read still
    /// in flight when a round ends leaves the session unresolved, and an unresolved player is left
    /// out of the team average rather than being averaged in at a guessed value.
    /// </remarks>
    public Task<double?> ReadAsync(ulong steamId)
    {
        if (!IsReady) return Task.FromResult<double?>(null);

        if (_cache.TryGetValue(steamId, out var cached)) return Task.FromResult<double?>(cached);

        return Task.Run<double?>(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT rating FROM {_table} WHERE steam_id = @steam_id";
                Add(command, "@steam_id", (long)steamId);

                var value = command.ExecuteScalar();
                if (value is null or DBNull) return null;

                var rating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                _cache[steamId] = rating;

                return rating;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: could not read the stored rating for {SteamId}.", steamId);
                return null;
            }
        });
    }

    /// <summary>Queues settled ratings for writing.</summary>
    /// <remarks>
    /// A failed batch merges back into the retry buffer off-thread rather than into the live
    /// sessions, which belong to the game thread.
    /// </remarks>
    public Task CommitAsync(List<RatingWrite> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        foreach (var write in batch) _cache[write.SteamId] = write.Rating;

        return Task.Run(() =>
        {
            List<RatingWrite> pending;

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
                _logger.LogError(ex, "Ranks_EloPoints: writing {Count} settled rating(s) failed; they are buffered for retry.", pending.Count);
            }
        });
    }

    private void Write(List<RatingWrite> batch)
    {
        using var connection = _core.Database.GetConnection(_connectionName);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var write in batch)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            // rating is replaced, the two counters accumulate. VALUES() rather than MySQL
            // 8.0.20's "AS new" row alias, which MariaDB does not support at all.
            command.CommandText = _mysql
                ? $"""
                   INSERT INTO {_table} (steam_id, rating, matches, abandons, updated_at)
                   VALUES (@steam_id, @rating, 1, @abandons, @updated_at)
                   ON DUPLICATE KEY UPDATE
                       rating = VALUES(rating),
                       matches = matches + 1,
                       abandons = abandons + VALUES(abandons),
                       updated_at = VALUES(updated_at)
                   """
                // Qualified with the table name, or the reference is ambiguous against EXCLUDED.
                : $"""
                   INSERT INTO {_table} (steam_id, rating, matches, abandons, updated_at)
                   VALUES (@steam_id, @rating, 1, @abandons, @updated_at)
                   ON CONFLICT (steam_id) DO UPDATE SET
                       rating = EXCLUDED.rating,
                       matches = {_table}.matches + 1,
                       abandons = {_table}.abandons + EXCLUDED.abandons,
                       updated_at = EXCLUDED.updated_at
                   """;

            Add(command, "@steam_id", (long)write.SteamId);
            Add(command, "@rating", (decimal)write.Rating);
            Add(command, "@abandons", write.Abandoned ? 1 : 0);
            Add(command, "@updated_at", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Deletes rows for purged players.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — the table is keyed on
    /// SteamID64 with nothing joining it back to core, so nothing else sweeps it.
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToArray();
        foreach (var id in ids) _cache.TryRemove(id, out _);

        return Task.Run(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (var steamId in ids)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {_table} WHERE steam_id = @steam_id";
                    Add(command, "@steam_id", (long)steamId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: failed to sweep {Count} purged player(s).", ids.Length);
            }
        });
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

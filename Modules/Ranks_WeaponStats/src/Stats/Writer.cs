using System.Data;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.WeaponStats.Stats;

/// <summary>Writes accumulated deltas additively. Off-thread, never touches a live session.</summary>
/// <remarks>
/// Core writes <em>absolute</em> values because its in-memory record is authoritative. This module
/// holds only session deltas, so additive is the right shape rather than a shortcut: there is no
/// read on connect, and two servers sharing a database accumulate instead of clobbering.
/// </remarks>
internal sealed class Writer
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _retryGate = new();
    private readonly List<StatsDelta> _retry = [];

    private string _connectionName = "default";
    private string _weaponTable = "ranks_weapon_stats";
    private string _hitTable = "ranks_hit_stats";
    private string _weaponHitTable = "ranks_weapon_hit_stats";
    private bool _mysql;

    public Writer(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the writer refuses to write.</summary>
    public void Configure(string connectionName, string weaponTable, string hitTable, string weaponHitTable)
    {
        _connectionName = connectionName;
        _weaponTable = weaponTable;
        _hitTable = hitTable;
        _weaponHitTable = weaponHitTable;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    /// <summary>Queues a batch for the next flush.</summary>
    /// <remarks>
    /// Merged into the retry buffer off-thread rather than back into the live session objects,
    /// which belong to the game thread.
    /// </remarks>
    public Task FlushAsync(List<StatsDelta> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            List<StatsDelta> pending;

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
                _logger.LogError(ex, "Ranks_WeaponStats: flush of {Count} player batch(es) failed; they are buffered for retry.", pending.Count);
            }
        });
    }

    private void Write(List<StatsDelta> batch)
    {
        using var connection = _core.Database.GetConnection(_connectionName);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var delta in batch)
        {
            foreach (var (weapon, counters) in delta.Weapons)
                WriteWeapon(connection, transaction, delta.SteamId, weapon, counters);

            foreach (var (hitGroup, hits, damage) in delta.HitGroups)
                WriteHitGroup(connection, transaction, delta.SteamId, hitGroup, hits, damage);

            foreach (var (weapon, hitGroup, hits, damage) in delta.WeaponHitGroups)
                WriteWeaponHitGroup(connection, transaction, delta.SteamId, weapon, hitGroup, hits, damage);
        }

        transaction.Commit();
    }

    private void WriteWeapon(IDbConnection connection, IDbTransaction transaction, ulong steamId, string weapon, WeaponCounters counters)
    {
        const string Columns = "steam_id, weapon, shots, hits, misses, damage, kills, deaths, headshots";
        const string Values = "@steam_id, @weapon, @shots, @hits, @misses, @damage, @kills, @deaths, @headshots";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // VALUES() rather than MySQL 8.0.20's "AS new" row alias: MariaDB does not support the
        // alias at all, and VALUES() works on both.
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_weaponTable} ({Columns}) VALUES ({Values})
               ON DUPLICATE KEY UPDATE
                   shots = shots + VALUES(shots), hits = hits + VALUES(hits),
                   misses = misses + VALUES(misses),
                   damage = damage + VALUES(damage), kills = kills + VALUES(kills),
                   deaths = deaths + VALUES(deaths), headshots = headshots + VALUES(headshots)
               """
            // Qualified with the table name, or the reference is ambiguous against EXCLUDED.
            : $"""
               INSERT INTO {_weaponTable} ({Columns}) VALUES ({Values})
               ON CONFLICT (steam_id, weapon) DO UPDATE SET
                   shots = {_weaponTable}.shots + EXCLUDED.shots,
                   hits = {_weaponTable}.hits + EXCLUDED.hits,
                   misses = {_weaponTable}.misses + EXCLUDED.misses,
                   damage = {_weaponTable}.damage + EXCLUDED.damage,
                   kills = {_weaponTable}.kills + EXCLUDED.kills,
                   deaths = {_weaponTable}.deaths + EXCLUDED.deaths,
                   headshots = {_weaponTable}.headshots + EXCLUDED.headshots
               """;

        Add(command, "@steam_id", (long)steamId);
        Add(command, "@weapon", weapon);
        Add(command, "@shots", counters.Shots);
        Add(command, "@hits", counters.Hits);
        Add(command, "@misses", counters.Misses);
        Add(command, "@damage", counters.Damage);
        Add(command, "@kills", counters.Kills);
        Add(command, "@deaths", counters.Deaths);
        Add(command, "@headshots", counters.Headshots);

        command.ExecuteNonQuery();
    }

    private void WriteHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, short hitGroup, long hits, long damage)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_hitTable} (steam_id, hitgroup, hits, damage)
               VALUES (@steam_id, @hitgroup, @hits, @damage)
               ON DUPLICATE KEY UPDATE hits = hits + VALUES(hits), damage = damage + VALUES(damage)
               """
            : $"""
               INSERT INTO {_hitTable} (steam_id, hitgroup, hits, damage)
               VALUES (@steam_id, @hitgroup, @hits, @damage)
               ON CONFLICT (steam_id, hitgroup) DO UPDATE SET
                   hits = {_hitTable}.hits + EXCLUDED.hits,
                   damage = {_hitTable}.damage + EXCLUDED.damage
               """;

        Add(command, "@steam_id", (long)steamId);
        Add(command, "@hitgroup", hitGroup);
        Add(command, "@hits", hits);
        Add(command, "@damage", damage);

        command.ExecuteNonQuery();
    }

    private void WriteWeaponHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, string weapon, short hitGroup, long hits, long damage)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_weaponHitTable} (steam_id, weapon, hitgroup, hits, damage)
               VALUES (@steam_id, @weapon, @hitgroup, @hits, @damage)
               ON DUPLICATE KEY UPDATE hits = hits + VALUES(hits), damage = damage + VALUES(damage)
               """
            : $"""
               INSERT INTO {_weaponHitTable} (steam_id, weapon, hitgroup, hits, damage)
               VALUES (@steam_id, @weapon, @hitgroup, @hits, @damage)
               ON CONFLICT (steam_id, weapon, hitgroup) DO UPDATE SET
                   hits = {_weaponHitTable}.hits + EXCLUDED.hits,
                   damage = {_weaponHitTable}.damage + EXCLUDED.damage
               """;

        Add(command, "@steam_id", (long)steamId);
        Add(command, "@weapon", weapon);
        Add(command, "@hitgroup", hitGroup);
        Add(command, "@hits", hits);
        Add(command, "@damage", damage);

        command.ExecuteNonQuery();
    }

    /// <summary>Deletes both tables' rows for purged players.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — module tables are keyed
    /// on SteamID64 with nothing joining them back to core, so nothing else sweeps them.
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToArray();

        return Task.Run(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (var table in (ReadOnlySpan<string>)[_weaponTable, _hitTable, _weaponHitTable])
                    foreach (var steamId in ids)
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"DELETE FROM {table} WHERE steam_id = @steam_id";
                        Add(command, "@steam_id", (long)steamId);
                        command.ExecuteNonQuery();
                    }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_WeaponStats: failed to sweep {Count} purged player(s).", ids.Length);
            }
        });
    }

    /// <summary>Deletes rows for players core no longer knows about.</summary>
    /// <remarks>
    /// PlayersPurged is a live event, so a purge that happens while this module is unloaded is
    /// never swept and those rows grow without bound. Safe to depend on core's table here:
    /// FR-DB-04 fixes ranks_players as a closed, known schema, which makes it a contract rather
    /// than an implementation detail.
    /// </remarks>
    public Task ReconcileOrphansAsync(string playersTable)
    {
        if (!IsReady) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                var removed = 0;

                foreach (var table in (ReadOnlySpan<string>)[_weaponTable, _hitTable, _weaponHitTable])
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"DELETE FROM {table} WHERE steam_id NOT IN (SELECT steam_id FROM {playersTable})";
                    removed += command.ExecuteNonQuery();
                }

                if (removed > 0)
                    _logger.LogInformation("Ranks_WeaponStats: removed {Count} orphaned row(s) left by a purge this module missed.", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_WeaponStats: orphan reconcile failed.");
            }
        });
    }

    /// <summary>Every stored weapon row for a player. Off-thread.</summary>
    /// <remarks>
    /// Returns what was last flushed, so the caller must add the live session delta — see
    /// <c>Collector.PeekFor</c>. Unfiltered and unordered, unlike <see cref="Top"/>, which exists
    /// to render a chat table.
    /// </remarks>
    public Task<List<(string Weapon, long Shots, long Hits, long Misses, long Damage, long Kills, long Deaths, long Headshots)>> ReadWeaponsAsync(ulong steamId)
    {
        if (!IsReady) return Task.FromResult(new List<(string, long, long, long, long, long, long, long)>());

        return Task.Run(() =>
        {
            var rows = new List<(string, long, long, long, long, long, long, long)>();

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT weapon, shots, hits, misses, damage, kills, deaths, headshots FROM {_weaponTable} WHERE steam_id = @steam_id";
            Add(command, "@steam_id", (long)steamId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                          reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));

            return rows;
        });
    }

    /// <summary>Every stored hitgroup row for a player. Off-thread.</summary>
    public Task<List<(short HitGroup, long Hits, long Damage)>> ReadHitGroupsAsync(ulong steamId)
    {
        if (!IsReady) return Task.FromResult(new List<(short, long, long)>());

        return Task.Run(() =>
        {
            var rows = new List<(short, long, long)>();

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT hitgroup, hits, damage FROM {_hitTable} WHERE steam_id = @steam_id";
            Add(command, "@steam_id", (long)steamId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2)));

            return rows;
        });
    }

    /// <summary>Every stored per-weapon hitgroup row for a player. Off-thread.</summary>
    public Task<List<(string Weapon, short HitGroup, long Hits, long Damage)>> ReadWeaponHitGroupsAsync(ulong steamId)
    {
        if (!IsReady) return Task.FromResult(new List<(string, short, long, long)>());

        return Task.Run(() =>
        {
            var rows = new List<(string, short, long, long)>();

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT weapon, hitgroup, hits, damage FROM {_weaponHitTable} WHERE steam_id = @steam_id";
            Add(command, "@steam_id", (long)steamId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt16(1), reader.GetInt64(2), reader.GetInt64(3)));

            return rows;
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

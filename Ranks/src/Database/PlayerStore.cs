using System.Data;
using Ranks.Players;

namespace Ranks.Database;

/// <summary>Maps the stat keys the API exposes onto core's fixed columns.</summary>
/// <remarks>
/// A whitelist, not an escaping routine: <c>statKey</c> arrives from module code and is
/// concatenated into an ORDER BY, which no parameter can carry. Anything not listed here is
/// refused rather than sanitised.
/// </remarks>
internal static class StatColumns
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["points"] = "points",
        ["core.points"] = "points",
        ["core.kills"] = "kills",
        ["core.deaths"] = "deaths",
        ["core.assists"] = "assists",
        ["core.round_wins"] = "round_wins",
        ["core.round_losses"] = "round_losses",
        ["core.match_wins"] = "match_wins",
        ["core.match_losses"] = "match_losses",
        ["core.bombs_planted"] = "bombs_planted",
        ["core.bombs_defused"] = "bombs_defused",
    };

    public static bool TryResolve(string statKey, out string column) => Map.TryGetValue(statKey, out column!);

    /// <summary>Reads a counter straight off a resident record.</summary>
    /// <remarks>
    /// Stat keys keep the <c>core.</c> prefix that point-event keys dropped, so <c>kill</c> is not
    /// an error here and returns 0 forever, which is the documented behaviour (FR-API-03).
    /// </remarks>
    public static long Read(PlayerRecord record, string statKey) => statKey.ToLowerInvariant() switch
    {
        "points" or "core.points" => record.Points,
        "core.kills" => record.Kills,
        "core.deaths" => record.Deaths,
        "core.assists" => record.Assists,
        "core.round_wins" => record.RoundWins,
        "core.round_losses" => record.RoundLosses,
        "core.match_wins" => record.MatchWins,
        "core.match_losses" => record.MatchLosses,
        "core.bombs_planted" => record.BombsPlanted,
        "core.bombs_defused" => record.BombsDefused,
        _ => 0,
    };
}

/// <summary>Reads and writes core's two tables. The single writer for <c>ranks_players</c>.</summary>
internal sealed class PlayerStore
{
    private readonly DatabaseConnection _database;
    private readonly TableNames _tables;
    private readonly bool _mysql;

    public PlayerStore(DatabaseConnection database, TableNames tables)
    {
        _database = database;
        _tables = tables;
        _mysql = database.Info.Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
    }

    public string PlayersTable => _tables.Resolve("ranks_players");

    public string SettingsTable => _tables.Resolve("ranks_settings");

    /// <summary>Loads one player's whole record, both tables in one round trip.</summary>
    /// <remarks>
    /// Returns null only when the player has no row yet; a <em>failure</em> throws, because a
    /// failed load must leave the player untracked and retry rather than substitute an empty
    /// record whose next absolute save would wipe the real row (FR-DB-07).
    /// </remarks>
    public PlayerRecord? Load(ulong steamId)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.steam_id, p.name, p.points, p.kills, p.deaths, p.assists,
                   p.round_wins, p.round_losses, p.match_wins, p.match_losses,
                   p.bombs_planted, p.bombs_defused, p.first_seen, p.last_seen,
                   s.show_point_messages
              FROM {PlayersTable} p
              LEFT JOIN {SettingsTable} s ON s.steam_id = p.steam_id
             WHERE p.steam_id = @steam_id
            """;
        AddParameter(command, "@steam_id", (long)steamId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new PlayerRecord
        {
            SteamId = steamId,
            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Points = reader.GetInt32(2),
            Kills = reader.GetInt32(3),
            Deaths = reader.GetInt32(4),
            Assists = reader.GetInt32(5),
            RoundWins = reader.GetInt32(6),
            RoundLosses = reader.GetInt32(7),
            MatchWins = reader.GetInt32(8),
            MatchLosses = reader.GetInt32(9),
            BombsPlanted = reader.GetInt32(10),
            BombsDefused = reader.GetInt32(11),
            FirstSeen = reader.GetDateTime(12),
            LastSeen = reader.GetDateTime(13),
            ShowPointMessages = reader.IsDBNull(14) || Convert.ToBoolean(reader.GetValue(14)),
        };
    }

    /// <summary>Writes a batch of records absolutely, isolating each behind a savepoint.</summary>
    /// <remarks>
    /// One bad row — an unencodable name is the realistic case — skips that player rather than
    /// aborting the batch, or one corrupt record stops the whole server from saving. Only committed
    /// SteamIDs come back, so the caller marks exactly those clean and the rest retry with their
    /// then-latest values.
    /// </remarks>
    public SaveOutcome Save(IReadOnlyList<PlayerSnapshotRow> rows)
    {
        if (rows.Count == 0) return new SaveOutcome([], []);

        var committed = new List<ulong>(rows.Count);
        var failed = new List<(ulong SteamId, Exception Error)>();

        using var connection = _database.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var savepoint = $"ranks_sp_{i}";

            try
            {
                Execute(connection, transaction, $"SAVEPOINT {savepoint}");
                WritePlayer(connection, transaction, row);
                WriteSettings(connection, transaction, row);
                Execute(connection, transaction, $"RELEASE SAVEPOINT {savepoint}");

                committed.Add(row.SteamId);
            }
            catch (Exception ex)
            {
                Execute(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint}");
                failed.Add((row.SteamId, ex));
            }
        }

        transaction.Commit();

        return new SaveOutcome(committed, failed);
    }

    /// <summary>Top rows by one column, carrying points so the caller can resolve a rank tier.</summary>
    /// <remarks>
    /// Points come back even when the sort is on another column: an offline player has no resident
    /// record, so without them every row of a kills leaderboard would render as the lowest tier.
    /// </remarks>
    public List<(ulong SteamId, string Name, long Value, int Points)> Top(string column, int limit)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT steam_id, name, {column}, points FROM {PlayersTable}
             ORDER BY {column} DESC, steam_id ASC
             LIMIT @limit
            """;
        AddParameter(command, "@limit", limit);

        var rows = new List<(ulong, string, long, int)>(limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(((ulong)reader.GetInt64(0),
                      reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                      Convert.ToInt64(reader.GetValue(2)),
                      reader.GetInt32(3)));

        return rows;
    }

    /// <remarks>Counts strictly-better rows, so equal values share a position rather than ordering arbitrarily.</remarks>
    public int Position(ulong steamId, string column)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) + 1 FROM {PlayersTable}
             WHERE {column} > (SELECT {column} FROM {PlayersTable} WHERE steam_id = @steam_id)
            """;
        AddParameter(command, "@steam_id", (long)steamId);

        var result = command.ExecuteScalar();

        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public int TotalRanked()
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {PlayersTable}";

        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>Deletes players unseen for longer than the threshold, cascading to settings.</summary>
    /// <remarks>
    /// Returns the purged SteamIDs because nothing else ever will: module tables are keyed on
    /// SteamID64 with nothing joining them back to core, so without this list their rows orphan
    /// permanently (FR-DB-08).
    /// </remarks>
    public List<ulong> Purge(int purgeDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-purgeDays);
        var purged = new List<ulong>();

        using var connection = _database.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT steam_id FROM {PlayersTable} WHERE last_seen < @cutoff";
            AddParameter(select, "@cutoff", cutoff);

            using var reader = select.ExecuteReader();
            while (reader.Read()) purged.Add((ulong)reader.GetInt64(0));
        }

        if (purged.Count == 0)
        {
            transaction.Rollback();
            return purged;
        }

        // Settings first, while the players rows it selects from still exist.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {SettingsTable} WHERE steam_id IN " +
                                 $"(SELECT steam_id FROM {PlayersTable} WHERE last_seen < @cutoff)";
            AddParameter(delete, "@cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        // Filtered directly rather than by subquery: MySQL refuses "DELETE FROM t WHERE x IN
        // (SELECT ... FROM t)" with "You can't specify target table for update in FROM clause".
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {PlayersTable} WHERE last_seen < @cutoff";
            AddParameter(delete, "@cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();

        return purged;
    }

    private void WritePlayer(IDbConnection connection, IDbTransaction transaction, PlayerSnapshotRow row)
    {
        const string Columns = "steam_id, name, points, kills, deaths, assists, round_wins, round_losses, " +
                               "match_wins, match_losses, bombs_planted, bombs_defused, first_seen, last_seen";
        const string Values = "@steam_id, @name, @points, @kills, @deaths, @assists, @round_wins, @round_losses, " +
                              "@match_wins, @match_losses, @bombs_planted, @bombs_defused, @first_seen, @last_seen";

        // first_seen is deliberately absent from both update lists: it is set once, and an absolute
        // writer that restated it would move it forward on every save.
        const string Assignments = "name = @name, points = @points, kills = @kills, deaths = @deaths, " +
                                   "assists = @assists, round_wins = @round_wins, round_losses = @round_losses, " +
                                   "match_wins = @match_wins, match_losses = @match_losses, " +
                                   "bombs_planted = @bombs_planted, bombs_defused = @bombs_defused, " +
                                   "last_seen = @last_seen";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"INSERT INTO {PlayersTable} ({Columns}) VALUES ({Values}) ON DUPLICATE KEY UPDATE {Assignments}"
            : $"INSERT INTO {PlayersTable} ({Columns}) VALUES ({Values}) ON CONFLICT (steam_id) DO UPDATE SET {Assignments}";

        AddParameter(command, "@steam_id", (long)row.SteamId);
        AddParameter(command, "@name", row.Name);
        AddParameter(command, "@points", row.Points);
        AddParameter(command, "@kills", row.Kills);
        AddParameter(command, "@deaths", row.Deaths);
        AddParameter(command, "@assists", row.Assists);
        AddParameter(command, "@round_wins", row.RoundWins);
        AddParameter(command, "@round_losses", row.RoundLosses);
        AddParameter(command, "@match_wins", row.MatchWins);
        AddParameter(command, "@match_losses", row.MatchLosses);
        AddParameter(command, "@bombs_planted", row.BombsPlanted);
        AddParameter(command, "@bombs_defused", row.BombsDefused);
        AddParameter(command, "@first_seen", row.FirstSeen);
        AddParameter(command, "@last_seen", row.LastSeen);

        command.ExecuteNonQuery();
    }

    private void WriteSettings(IDbConnection connection, IDbTransaction transaction, PlayerSnapshotRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"INSERT INTO {SettingsTable} (steam_id, show_point_messages) VALUES (@steam_id, @show) " +
              "ON DUPLICATE KEY UPDATE show_point_messages = @show"
            : $"INSERT INTO {SettingsTable} (steam_id, show_point_messages) VALUES (@steam_id, @show) " +
              "ON CONFLICT (steam_id) DO UPDATE SET show_point_messages = @show";

        AddParameter(command, "@steam_id", (long)row.SteamId);
        AddParameter(command, "@show", row.ShowPointMessages);

        command.ExecuteNonQuery();
    }

    private static void Execute(IDbConnection connection, IDbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Generic DbParameter rather than a provider type, so one code path serves all three engines.
    /// '@' is accepted as the marker by MySqlConnector, Npgsql and System.Data.SQLite alike.
    /// </remarks>
    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>What one save pass committed and what it skipped.</summary>
internal sealed record SaveOutcome(
    IReadOnlyList<ulong> Committed,
    IReadOnlyList<(ulong SteamId, Exception Error)> Failed);

/// <summary>An immutable copy of a record, taken on the game thread and written off it.</summary>
/// <remarks>
/// Snapshotting is what makes the writer race-free: the record keeps being mutated by gameplay
/// while the write is in flight, and an absolute write of a half-mutated row would persist a state
/// that never existed.
/// </remarks>
internal readonly record struct PlayerSnapshotRow(
    ulong SteamId,
    string Name,
    int Points,
    int Kills,
    int Deaths,
    int Assists,
    int RoundWins,
    int RoundLosses,
    int MatchWins,
    int MatchLosses,
    int BombsPlanted,
    int BombsDefused,
    DateTime FirstSeen,
    DateTime LastSeen,
    bool ShowPointMessages)
{
    public static PlayerSnapshotRow From(PlayerRecord record) => new(
        record.SteamId, record.Name, record.Points, record.Kills, record.Deaths, record.Assists,
        record.RoundWins, record.RoundLosses, record.MatchWins, record.MatchLosses,
        record.BombsPlanted, record.BombsDefused, record.FirstSeen, record.LastSeen,
        record.ShowPointMessages);
}

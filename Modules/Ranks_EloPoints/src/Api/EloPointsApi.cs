using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Match;
using Ranks.Modules.Contract;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Names its contract without JIT isolation, unlike <see cref="RanksBridge"/>: a provider
/// hard-requires the assembly it publishes from, exactly as core does with Ranks.Contract.
///
/// No session merge is needed here, unlike WeaponStats — a rating is written once, at match end,
/// so the stored value is never behind. What a match in progress would contribute is deliberately
/// not exposed: it has no result until it ends.
/// </remarks>
internal sealed class EloPointsApi : IEloPointsApi
{
    private readonly ISwiftlyCore _core;
    private readonly MatchTracker _tracker;
    private readonly ILogger _logger;

    private string _connectionName = "default";
    private string _table = "ranks_elopoints";
    private bool _ready;

    public EloPointsApi(ISwiftlyCore core, MatchTracker tracker, ILogger logger)
    {
        _core = core;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>Binds the names core resolved. Until this runs every query answers empty.</summary>
    public void Configure(string connectionName, string table)
    {
        _connectionName = connectionName;
        _table = table;
        _ready = true;
    }

    public Task<EloStanding?> GetStandingAsync(ulong steamId)
    {
        if (!_ready) return Task.FromResult<EloStanding?>(null);

        return Task.Run<EloStanding?>(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT rating, matches, abandons, updated_at FROM {_table} WHERE steam_id = @steam_id";
                Add(command, "@steam_id", (long)steamId);

                using var reader = command.ExecuteReader();

                return reader.Read() ? Read(steamId, reader) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: could not read the standing for {SteamId}.", steamId);
                return null;
            }
        });
    }

    public Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit = 10)
    {
        if (!_ready) return Task.FromResult<IReadOnlyList<EloStanding>>([]);

        var clamped = Math.Clamp(limit, 1, 100);

        return Task.Run<IReadOnlyList<EloStanding>>(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT steam_id, rating, matches, abandons, updated_at FROM {_table} ORDER BY rating DESC LIMIT @limit";
                Add(command, "@limit", clamped);

                var rows = new List<EloStanding>(clamped);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(new EloStanding(
                        (ulong)reader.GetInt64(0),
                        reader.GetDecimal(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetDateTime(4)));

                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: could not read the rating leaderboard.");
                return [];
            }
        });
    }

    public bool IsInRatedMatch(ulong steamId) => _tracker.HasOpenSession(steamId);

    private static EloStanding Read(ulong steamId, IDataReader reader) => new(
        steamId,
        Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.GetDateTime(3));

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

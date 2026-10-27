using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Config;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace Ranks.WeaponStats.Commands;

/// <summary>Registers and handles this module's own command.</summary>
/// <remarks>
/// The module ships its own command rather than contributing to core's <c>!rank</c> menu: core's
/// menus are fixed, because composing them from whatever is installed is the dependency inversion
/// FR-CMD-04 rules out.
/// </remarks>
internal sealed class StatsCommands
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    private volatile WeaponStatsConfig _config;

    public StatsCommands(ISwiftlyCore core, Writer writer, WeaponStatsConfig config, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _config = config;
        _logger = logger;
    }

    public void UpdateConfig(WeaponStatsConfig config) => _config = config;

    public void RegisterAll(CommandsConfig commands)
    {
        foreach (var (key, definition) in commands.All)
        {
            if (string.IsNullOrWhiteSpace(definition.Command)) continue;
            if (key != CommandsConfig.WeaponStats) continue;

            try
            {
                // registerRaw stays false: the framework owns the sw_ console prefix and the chat
                // triggers alike, and taking the bare console name would collide with anything
                // else that wants it.
                _registered.Add(_core.Command.RegisterCommand(
                    definition.Command, OnWeaponStats, registerRaw: false,
                    definition.Permission, _core.Localizer[$"command.{key}.help"]));

                foreach (var alias in definition.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                    _core.Command.RegisterCommandAlias(definition.Command, alias, registerRaw: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_WeaponStats: could not register command '{Command}'.", definition.Command);
            }
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            try
            {
                _core.Command.UnregisterCommand(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ranks_WeaponStats: could not unregister a command.");
            }
        }

        _registered.Clear();
    }

    private void OnWeaponStats(ICommandContext ctx)
    {
        if (!ctx.IsSentByPlayer || ctx.Sender is not { } player)
        {
            ctx.Reply(_core.Localizer["error.players_only"]);
            return;
        }

        if (!_writer.IsReady)
        {
            player.SendChat(_core.Localizer["error.unavailable"]);
            return;
        }

        var config = _config;
        var steamId = player.SteamID;

        // Off the game thread, then back: the query is database I/O, and the reply needs the
        // player again afterwards.
        Task.Run(() =>
        {
            try
            {
                var rows = _writer.Top(steamId, config.Display.TopWeapons, config.Display.MinShotsToList);

                _core.Scheduler.NextTick(() => Reply(steamId, rows));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_WeaponStats: weapon stats query failed for {SteamId}.", steamId);
            }
        });
    }

    private void Reply(ulong steamId, List<(string Weapon, long Shots, long Hits, long Misses, long Damage, long Kills, long Deaths, long Headshots)> rows)
    {
        var player = _core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: false);
        if (player is null) return;

        // A player with no rows gets a line saying so, not an empty table.
        if (rows.Count == 0)
        {
            player.SendChat(_core.Localizer["stats.empty"]);
            return;
        }

        player.SendChat(_core.Localizer["stats.header"]);

        foreach (var row in rows)
        {
            var accuracy = row.Shots == 0 ? 0 : Math.Round(row.Hits * 100.0 / row.Shots, 1);
            var headshotRate = row.Kills == 0 ? 0 : Math.Round(row.Headshots * 100.0 / row.Kills, 1);

            player.SendChat(_core.Localizer["stats.row", Display(row.Weapon), row.Kills, accuracy, headshotRate, row.Damage]);
        }
    }

    /// <remarks>
    /// Strips the stored designer-name prefix for display only. The stored form stays
    /// <c>weapon_ak47</c>, which is what the entity system, the dumps and weapon_fire all agree on.
    /// </remarks>
    private static string Display(string weapon) =>
        weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon[7..] : weapon;
}

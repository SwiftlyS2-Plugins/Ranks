using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Config;
using Ranks.WeaponStats.Menus;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace Ranks.WeaponStats.Commands;

/// <summary>Registers and handles this module's own command.</summary>
/// <remarks>
/// A shortcut, not a second surface: it opens the same menu core nests under <c>Statistics</c>,
/// so the two can never disagree about what a weapon's numbers are.
/// </remarks>
internal sealed class StatsCommands
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly WeaponStatsMenus _menus;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    public StatsCommands(ISwiftlyCore core, Writer writer, WeaponStatsMenus menus, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _menus = menus;
        _logger = logger;
    }

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

        // No Task.Run here any more: the menu's paged component owns the query and the thread it
        // runs on, so opening is a game-thread call that returns immediately.
        _menus.OpenWeapons(player);
    }
}

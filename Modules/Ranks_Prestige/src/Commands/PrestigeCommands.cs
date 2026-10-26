using Microsoft.Extensions.Logging;
using Ranks.Prestige.Api;
using Ranks.Prestige.Config;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace Ranks.Prestige.Commands;

/// <summary>Registers and handles this module's own command.</summary>
/// <remarks>
/// The module ships its own command rather than contributing to core's <c>!rank</c> menu: core's
/// menus are fixed, because composing them from whatever is installed is the dependency inversion
/// FR-CMD-04 rules out.
/// </remarks>
internal sealed class PrestigeCommands
{
    private readonly ISwiftlyCore _core;
    private readonly RanksBridge _bridge;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    private volatile PrestigeConfig _config;

    public PrestigeCommands(ISwiftlyCore core, RanksBridge bridge, PrestigeConfig config, ILogger logger)
    {
        _core = core;
        _bridge = bridge;
        _config = config;
        _logger = logger;
    }

    public void UpdateConfig(PrestigeConfig config) => _config = config;

    public void RegisterAll(CommandsConfig commands)
    {
        foreach (var (key, definition) in commands.All)
        {
            if (string.IsNullOrWhiteSpace(definition.Command)) continue;
            if (key != CommandsConfig.Prestige) continue;

            try
            {
                // registerRaw stays false: the framework owns the sw_ console prefix and the chat
                // triggers alike, and taking the bare console name would collide with anything
                // else that wants it.
                _registered.Add(_core.Command.RegisterCommand(
                    definition.Command, OnPrestige, registerRaw: false,
                    definition.Permission, _core.Localizer[$"command.{key}.help"]));

                foreach (var alias in definition.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                    _core.Command.RegisterCommandAlias(definition.Command, alias, registerRaw: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_Prestige: could not register command '{Command}'.", definition.Command);
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
                _logger.LogWarning(ex, "Ranks_Prestige: could not unregister a command.");
            }
        }

        _registered.Clear();
    }

    private void OnPrestige(ICommandContext ctx)
    {
        if (!ctx.IsSentByPlayer || ctx.Sender is not { } player)
        {
            ctx.Reply(_core.Localizer["error.players_only"]);
            return;
        }

        if (!_config.Enabled)
        {
            player.SendChat(_core.Localizer["prestige.disabled"]);
            return;
        }

        if (_bridge.StandingFor(player) is not { } standing)
        {
            player.SendChat(_core.Localizer["error.unavailable"]);
            return;
        }

        if (standing.Level == 0) player.SendChat(_core.Localizer["prestige.none"]);
        else player.SendChat(_core.Localizer["prestige.current", standing.Level, standing.DisplayPoints]);

        if (_bridge.PointsToNextFor(player) is { } remaining)
            player.SendChat(_core.Localizer["prestige.next", remaining]);
        else
            player.SendChat(_core.Localizer["prestige.max"]);
    }
}

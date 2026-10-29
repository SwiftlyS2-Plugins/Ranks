using Microsoft.Extensions.Logging;
using Ranks.Prestige.Api;
using Ranks.Prestige.Config;
using Ranks.Prestige.Menus;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace Ranks.Prestige.Commands;

/// <summary>Registers and handles this module's own command.</summary>
/// <remarks>
/// A shortcut, not a second surface: it opens the same menu core nests under <c>Statistics</c>.
/// </remarks>
internal sealed class PrestigeCommands
{
    private readonly ISwiftlyCore _core;
    private readonly PrestigeMenus _menus;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    private volatile PrestigeConfig _config;

    public PrestigeCommands(ISwiftlyCore core, PrestigeMenus menus, PrestigeConfig config, ILogger logger)
    {
        _core = core;
        _menus = menus;
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

        // Every state the chat version reported — disabled, unavailable, standing — is a row in
        // the menu, so the branching lives there rather than being duplicated here.
        _menus.Open(player);
    }
}

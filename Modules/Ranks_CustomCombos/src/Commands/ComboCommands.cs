using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Config;
using Ranks.CustomCombos.Menus;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace Ranks.CustomCombos.Commands;

/// <summary><c>!combos</c> — the caller's combo counts, highest first.</summary>
/// <remarks>
/// Reads through the published API rather than the writer: the API merges unflushed counts, and a
/// player checking straight after a kill would otherwise be told a figure a flush interval old —
/// while a consumer of the API saw the right one.
/// </remarks>
internal sealed class ComboCommands
{
    private readonly ISwiftlyCore _core;
    private readonly ComboMenus _menus;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    public ComboCommands(ISwiftlyCore core, ComboMenus menus, ILogger logger)
    {
        _core = core;
        _menus = menus;
        _logger = logger;
    }

    public void RegisterAll(CommandsConfig commands)
    {
        foreach (var (key, definition) in commands.All)
        {
            if (string.IsNullOrWhiteSpace(definition.Command)) continue;
            if (key != CommandsConfig.Combos) continue;

            try
            {
                // registerRaw stays false: the framework owns the sw_ console prefix and the chat
                // triggers alike, and taking the bare console name would collide with anything
                // else that wants it.
                _registered.Add(_core.Command.RegisterCommand(
                    definition.Command, OnCombos, registerRaw: false,
                    definition.Permission, _core.Localizer[$"command.{key}.help"]));

                foreach (var alias in definition.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                    _core.Command.RegisterCommandAlias(definition.Command, alias, registerRaw: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_CustomCombos: could not register command '{Command}'.", definition.Command);
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
                _logger.LogWarning(ex, "Ranks_CustomCombos: could not unregister a command.");
            }
        }

        _registered.Clear();
    }

    private void OnCombos(ICommandContext context)
    {
        if (context.Sender is not { } player) return;

        // The menu's paged component owns the query and the thread it runs on, so this is a
        // game-thread call that returns immediately.
        _menus.Open(player);
    }
}

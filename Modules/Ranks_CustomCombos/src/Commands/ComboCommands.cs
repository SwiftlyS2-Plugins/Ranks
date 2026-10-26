using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Api;
using Ranks.CustomCombos.Config;
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
    private readonly CustomCombosApi _api;
    private readonly ILogger _logger;
    private readonly List<Guid> _registered = [];

    public ComboCommands(ISwiftlyCore core, CustomCombosApi api, ILogger logger)
    {
        _core = core;
        _api = api;
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

        var steamId = player.SteamID;

        _ = Task.Run(async () =>
        {
            try
            {
                // Already ordered by count, descending.
                var rows = await _api.GetCombosAsync(steamId);

                // Back to the game thread before touching the player: the read ran off it.
                _core.Scheduler.NextTick(() =>
                {
                    if (player is not { Controller.IsValid: true }) return;

                    if (rows.Count == 0)
                    {
                        player.SendChat(_core.Localizer["combos.none"]);
                        return;
                    }

                    player.SendChat(_core.Localizer["combos.header"]);

                    foreach (var row in rows)
                        player.SendChat(_core.Localizer["combos.row", Describe(row.ComboKey), row.Count]);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_CustomCombos: combo query failed for {SteamId}.", steamId);
            }
        });
    }

    /// <remarks>Same missing-key fallback as the arbiter's chat line, for the same reason.</remarks>
    private string Describe(string comboKey)
    {
        var key = $"combo.{comboKey}";
        var resolved = _core.Localizer[key];

        return string.IsNullOrEmpty(resolved)
               || string.Equals(resolved, key, StringComparison.Ordinal)
               || resolved.EndsWith('.' + key, StringComparison.Ordinal)
            ? comboKey
            : resolved;
    }
}

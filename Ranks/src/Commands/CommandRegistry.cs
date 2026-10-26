using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Menus;
using Ranks.Players;
using Ranks.Ranking;
using Ranks.Scoreboard;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace Ranks.Commands;

/// <summary>Registers and handles core's commands.</summary>
/// <remarks>
/// Registered at runtime rather than by <c>[Command]</c>: attribute arguments are compile-time
/// constants, and FR-CMD-01 makes the name, permission and whether it registers at all
/// operator-editable. An attribute also cannot express "an empty Command string means do not
/// register".
/// </remarks>
internal sealed class CommandRegistry
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<CommandsConfig> _commands;
    private readonly RankMenus _menus;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly PlayerSessions _sessions;
    private readonly ScoreboardService _scoreboard;
    private readonly RanksApi _api;
    private readonly ILogger _logger;

    private readonly List<Guid> _registered = [];

    public CommandRegistry(
        ISwiftlyCore core,
        ConfigHost<CommandsConfig> commands,
        RankMenus menus,
        RankLadder ladder,
        PlayerRegistry registry,
        PlayerSessions sessions,
        ScoreboardService scoreboard,
        RanksApi api,
        ILogger logger)
    {
        _core = core;
        _commands = commands;
        _menus = menus;
        _ladder = ladder;
        _registry = registry;
        _sessions = sessions;
        _scoreboard = scoreboard;
        _api = api;
        _logger = logger;
    }

    public void RegisterAll()
    {
        foreach (var (key, definition) in _commands.Current.All)
        {
            if (string.IsNullOrWhiteSpace(definition.Command)) continue;

            var handler = HandlerFor(key);
            if (handler is null) continue;

            try
            {
                // registerRaw stays false: the framework owns the sw_ console prefix and the
                // !rank / /rank chat triggers alike. Registering raw takes the bare console name
                // for ourselves, which collides with anything else that wants it and bypasses the
                // convention every other SwiftlyS2 plugin follows.
                _registered.Add(_core.Command.RegisterCommand(
                    definition.Command, handler, registerRaw: false,
                    definition.Permission, _core.Localizer[$"command.{key}.help"]));

                foreach (var alias in definition.Aliases)
                    _core.Command.RegisterCommandAlias(definition.Command, alias, registerRaw: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: could not register command '{Command}'.", definition.Command);
            }
        }
    }

    /// <remarks>
    /// Plugins auto-unregister on unload, so this exists for a config reload that renames a
    /// command — the only way to drop the old name without a full restart.
    /// </remarks>
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
                _logger.LogWarning(ex, "Ranks: could not unregister a command.");
            }
        }

        _registered.Clear();
    }

    private ICommandService.CommandListener? HandlerFor(string key) => key switch
    {
        CommandsConfig.Rank => ctx => ForPlayer(ctx, _menus.OpenRank),
        CommandsConfig.Ranks => ctx => ForPlayer(ctx, _menus.OpenLadder),
        CommandsConfig.Stats => ctx => ForPlayer(ctx, _menus.ShowStats),
        CommandsConfig.Settings => ctx => ForPlayer(ctx, _menus.OpenSettings),
        CommandsConfig.Top => OnTop,
        CommandsConfig.TogglePointMsg => ctx => ForPlayer(ctx, TogglePointMessages),
        CommandsConfig.ResetMyRank => ctx => ForPlayer(ctx, ResetSelf),
        CommandsConfig.SetPoints => ctx => AdjustPoints(ctx, AdjustMode.Set),
        CommandsConfig.GivePoints => ctx => AdjustPoints(ctx, AdjustMode.Add),
        CommandsConfig.RemovePoints => ctx => AdjustPoints(ctx, AdjustMode.Subtract),
        CommandsConfig.ResetRank => ResetTarget,
        _ => null,
    };

    /// <remarks>
    /// Sender is null from the server console, so IsSentByPlayer has to be checked first rather
    /// than relying on a null check that the compiler cannot see through.
    /// </remarks>
    private void ForPlayer(ICommandContext ctx, Action<IPlayer> action)
    {
        if (!ctx.IsSentByPlayer || ctx.Sender is not { } player)
        {
            ctx.Reply(_core.Localizer["error.players_only"]);
            return;
        }

        action(player);
    }

    private void OnTop(ICommandContext ctx) => ForPlayer(ctx, player =>
        _menus.OpenTop(player, async () =>
        {
            var entries = await _api.GetTopAsync("points", 50).ConfigureAwait(false);

            return entries.Select(e => (e.Position, e.Name, e.Value)).ToList();
        }));

    private void TogglePointMessages(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record)
        {
            player.SendChat(_core.Localizer["error.not_loaded"]);
            return;
        }

        record.ShowPointMessages = !record.ShowPointMessages;
        record.Dirty = true;

        player.SendChat(_core.Localizer[record.ShowPointMessages ? "settings.point_messages_on" : "settings.point_messages_off"]);
    }

    /// <remarks>
    /// Two-step by design: the first call arms, the second within the window commits. A menu
    /// confirmation would be nicer, but this command is reachable from console where no menu is.
    /// </remarks>
    private void ResetSelf(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record)
        {
            player.SendChat(_core.Localizer["error.not_loaded"]);
            return;
        }

        var now = DateTime.UtcNow;

        if (_pendingResets.TryGetValue(record.SteamId, out var armedAt) && now - armedAt < ResetConfirmWindow)
        {
            _pendingResets.Remove(record.SteamId);
            ResetRecord(record);
            player.SendChat(_core.Localizer["reset.done"]);
            return;
        }

        _pendingResets[record.SteamId] = now;
        player.SendChat(_core.Localizer["reset.confirm", (int)ResetConfirmWindow.TotalSeconds]);
    }

    private static readonly TimeSpan ResetConfirmWindow = TimeSpan.FromSeconds(15);

    private readonly Dictionary<ulong, DateTime> _pendingResets = [];

    private void AdjustPoints(ICommandContext ctx, AdjustMode mode)
    {
        if (ctx.Args.Length < 3)
        {
            ctx.Reply(_core.Localizer["admin.usage", ctx.CommandName]);
            return;
        }

        if (!int.TryParse(ctx.Args[2], out var amount))
        {
            ctx.Reply(_core.Localizer["admin.bad_amount", ctx.Args[2]]);
            return;
        }

        foreach (var target in Targets(ctx, ctx.Args[1]))
        {
            if (_registry.BySlot(target.Slot) is not { } record) continue;

            var reason = $"admin.{mode.ToString().ToLowerInvariant()}";

            if (mode == AdjustMode.Set) _api.SetPoints(record.SteamId, amount, reason);
            else _api.AddPoints(record.SteamId, mode == AdjustMode.Add ? amount : -amount, reason, showMessage: false);

            ctx.Reply(_core.Localizer["admin.adjusted", record.Name, record.Points]);
        }
    }

    private void ResetTarget(ICommandContext ctx)
    {
        if (ctx.Args.Length < 2)
        {
            ctx.Reply(_core.Localizer["admin.usage", ctx.CommandName]);
            return;
        }

        foreach (var target in Targets(ctx, ctx.Args[1]))
        {
            if (_registry.BySlot(target.Slot) is not { } record) continue;

            ResetRecord(record);
            ctx.Reply(_core.Localizer["admin.reset", record.Name]);
        }
    }

    private IEnumerable<IPlayer> Targets(ICommandContext ctx, string pattern)
    {
        var matches = _core.PlayerManager
            .FindTargettedPlayers(ctx.Sender!, pattern, TargetSearchMode.IncludeSelf | TargetSearchMode.NoBots, StringComparison.OrdinalIgnoreCase)
            .Where(p => !p.IsFakeClient)
            .ToArray();

        if (matches.Length == 0) ctx.Reply(_core.Localizer["admin.no_target", pattern]);

        return matches;
    }

    /// <remarks>
    /// Resets points and the counters alike. The row is kept rather than deleted so first_seen
    /// survives and the player is not treated as new on their next connect.
    /// </remarks>
    private void ResetRecord(PlayerRecord record)
    {
        record.Points = 0;
        record.Kills = 0;
        record.Deaths = 0;
        record.Assists = 0;
        record.RoundWins = 0;
        record.RoundLosses = 0;
        record.MatchWins = 0;
        record.MatchLosses = 0;
        record.BombsPlanted = 0;
        record.BombsDefused = 0;
        record.RankIndex = _ladder.IndexFor(0);
        record.Dirty = true;

        _ = _sessions.SavePlayerAsync(record.SteamId);
        _scoreboard.Apply(record.SteamId);
    }

    private enum AdjustMode
    {
        Set,
        Add,
        Subtract,
    }
}

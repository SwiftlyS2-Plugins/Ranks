using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Contract;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.Menus;

/// <summary>Core's menu tree: one root of categories, everything else a submenu under it.</summary>
/// <remarks>
/// <para>
/// Built through <c>Core.Menu</c>, the 1.5.0 framework; the classic <c>Core.MenusAPI</c> surface is
/// <c>[Obsolete]</c>. A player has at most one open menu across every plugin, so opening one of
/// these closes someone else's — that is normal, not an error to guard against. Menus are released
/// automatically when the plugin unloads, so there is no teardown here.
/// </para>
/// <para>
/// Everything nests with <see cref="SubmenuComponent"/> rather than <c>ButtonComponent</c> +
/// <c>Open()</c>: <c>Open()</c> starts a new root with no back-stack, so the framework's configured
/// navigation would have nothing to go back to. The same rule is what lets a module's menu sit
/// under <c>Statistics</c> and still return here — a menu with no parent of its own is adopted by
/// the entry that opened it.
/// </para>
/// </remarks>
internal sealed class RankMenus
{
    private readonly ISwiftlyCore _core;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly PlayerReset _reset;
    private readonly MenuEntryRegistry _entries;
    private readonly RanksApi _api;
    private readonly ILogger _logger;

    public RankMenus(
        ISwiftlyCore core,
        RankLadder ladder,
        PlayerRegistry registry,
        PlayerReset reset,
        MenuEntryRegistry entries,
        RanksApi api,
        ILogger logger)
    {
        _core = core;
        _ladder = ladder;
        _registry = registry;
        _reset = reset;
        _entries = entries;
        _api = api;
        _logger = logger;
    }

    // ---- Entry points --------------------------------------------------------

    /// <summary>The one way in. Every other opener below is a shortcut to a branch of this.</summary>
    public void OpenRoot(IPlayer player)
    {
        if (!Loaded(player)) return;

        // Ids are namespaced because a menu id doubles as its keybind configuration scope, which
        // keeps the server's generated keybind file readable. Each one names what it shows.
        _core.Menu.CreateMenu("ranks.root")
            .WithTitle(_core.Localizer["menu.root.title"])
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer["menu.root.rank"], _ => BuildMyRank(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.stats"], _ => BuildStatistics(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.top"], _ => BuildTop()))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.settings"], _ => BuildSettings(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.help"], _ => BuildHelp()))
            .Build()
            .Open(player);
    }

    public void OpenStatistics(IPlayer player)
    {
        if (Loaded(player)) BuildStatistics(player)?.Open(player);
    }

    public void OpenLadder(IPlayer player) => BuildLadder(player).Open(player);

    public void OpenTop(IPlayer player) => BuildTop().Open(player);

    /// <remarks>Chat, not a menu: there is no record to render and the player needs to know why.</remarks>
    private bool Loaded(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not null) return true;

        Chat.Send(_core, player, "error.not_loaded");
        return false;
    }

    // ---- My rank -------------------------------------------------------------

    private IMenu? BuildMyRank(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return null;

        var tier = _ladder.TierFor(record.Points);
        var toNext = _ladder.PointsToNext(record.Points);
        var progress = (float)_ladder.ProgressInBand(record.Points);

        return _core.Menu.CreateMenu("ranks.myrank")
            .WithTitle(_core.Localizer["menu.rank.title"])
            .WithItemsPerPage(8)
            .Add(new TextComponent(_core.Localizer["menu.rank.current", tier.Name]))
            .Add(new TextComponent(_core.Localizer["menu.rank.XP", record.Points]))
            .Add(new ProgressBarComponent(
                toNext is { } remaining
                    ? _core.Localizer["menu.rank.next", _ladder.NextTier(record.Points)!.Name, remaining]
                    : _core.Localizer["menu.rank.top_band"],
                _ => progress,
                multiLine: false))
            .Add(new SubmenuComponent(_core.Localizer["menu.rank.ladder"], _ => BuildLadder(player)))
            .Build();
    }

    private IMenu BuildLadder(IPlayer player)
    {
        var index = _registry.BySlot(player.Slot)?.RankIndex ?? -1;

        var builder = _core.Menu.CreateMenu("ranks.ladder")
            .WithTitle(_core.Localizer["menu.ladder.title"])
            .WithItemsPerPage(8);

        var tiers = _ladder.Tiers;

        for (var i = 0; i < tiers.Count; i++)
        {
            var key = i == index ? "menu.ladder.row_current" : "menu.ladder.row";
            builder.Add(new TextComponent(_core.Localizer[key, tiers[i].Name, tiers[i].Points]));
        }

        return builder.Build();
    }

    // ---- Statistics ----------------------------------------------------------

    /// <summary>Core's own counters, plus one row per module registered under this section.</summary>
    private IMenu? BuildStatistics(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is null) return null;

        var builder = _core.Menu.CreateMenu("ranks.stats")
            .WithTitle(_core.Localizer["menu.stats.title"])
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer["menu.stats.main"], _ => BuildMainStats(player)));

        var entries = _entries.For(RanksMenuSections.Statistics);

        foreach (var (moduleId, entry) in entries)
            builder.Add(new SubmenuComponent(entry.Label, _ => _entries.Build(moduleId, entry, player)));

        // Says why the list is short, rather than leaving the player to read it as a broken menu.
        if (entries.Count == 0)
            builder.Add(new TextComponent(_core.Localizer["menu.stats.modules_empty"]));

        return builder.Build();
    }

    private IMenu? BuildMainStats(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return null;

        var kd = record.Deaths == 0 ? record.Kills : Math.Round(record.Kills / (double)record.Deaths, 2);
        var headshotRate = record.Kills == 0 ? 0 : Math.Round(record.Headshots * 100.0 / record.Kills, 1);

        // Hits + Misses, never a stored shot count: one shotgun blast is a single trigger pull
        // that can land nine hits, so these two columns are the only pair that adds up.
        var attempts = record.Hits + record.Misses;
        var accuracy = attempts == 0 ? 0 : Math.Round(record.Hits * 100.0 / attempts, 1);

        return _core.Menu.CreateMenu("ranks.stats.main")
            .WithTitle(_core.Localizer["menu.stats.main_title"])
            .WithItemsPerPage(9)
            .Add(new TextComponent(_core.Localizer["menu.stats.kills", record.Kills]))
            .Add(new TextComponent(_core.Localizer["menu.stats.deaths", record.Deaths]))
            .Add(new TextComponent(_core.Localizer["menu.stats.kd", kd]))
            .Add(new TextComponent(_core.Localizer["menu.stats.assists", record.Assists]))
            .Add(new TextComponent(_core.Localizer["menu.stats.headshots", record.Headshots, headshotRate]))
            .Add(new TextComponent(_core.Localizer["menu.stats.accuracy", accuracy, record.Hits, record.Misses]))
            .Add(new TextComponent(_core.Localizer["menu.stats.rounds", record.RoundWins, record.RoundLosses]))
            .Add(new TextComponent(_core.Localizer["menu.stats.matches", record.MatchWins, record.MatchLosses]))
            .Add(new TextComponent(_core.Localizer["menu.stats.bombs", record.BombsPlanted, record.BombsDefused]))
            .Build();
    }

    // ---- Leaderboard ---------------------------------------------------------

    /// <summary>Paging is the component's job, so nothing here hand-rolls it.</summary>
    private IMenu BuildTop() =>
        _core.Menu.CreateMenu("ranks.top")
            .WithTitle(_core.Localizer["menu.top.title"])
            .WithItemsPerPage(10)
            .Add(new PagedCollectionComponent<IMenuComponent>(async () =>
            {
                try
                {
                    var rows = await _api.GetTopAsync("points", 50).ConfigureAwait(false);

                    return rows.Count == 0
                        ? [new TextComponent(_core.Localizer["menu.top.empty"])]
                        : rows.Select(r => (IMenuComponent)new TextComponent(
                            _core.Localizer["menu.top.row", r.Position, r.Name, r.Value])).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks: failed to build the leaderboard menu.");
                    return [new TextComponent(_core.Localizer["menu.top.error"])];
                }
            }))
            .Build();

    // ---- Settings ------------------------------------------------------------

    private IMenu? BuildSettings(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return null;

        var builder = _core.Menu.CreateMenu("ranks.settings")
            .WithTitle(_core.Localizer["menu.settings.title"])
            .WithItemsPerPage(8)
            .Add(new ToggleComponent(
                _core.Localizer["menu.settings.XP_messages"],
                record.ShowPointMessages,
                ctx =>
                {
                    record.ShowPointMessages = ctx.NewValue;
                    record.Dirty = true;
                }));

        foreach (var (moduleId, entry) in _entries.For(RanksMenuSections.Settings))
            builder.Add(new SubmenuComponent(entry.Label, _ => _entries.Build(moduleId, entry, player)));

        // Last, and behind its own screen: the only destructive row in the tree.
        builder.Add(new SubmenuComponent(_core.Localizer["menu.settings.reset"], _ => BuildResetConfirm(player)));

        return builder.Build();
    }

    private IMenu BuildResetConfirm(IPlayer player) =>
        _core.Menu.CreateMenu("ranks.settings.reset")
            .WithTitle(_core.Localizer["menu.settings.reset_title"])
            .Add(new TextComponent(_core.Localizer["menu.settings.reset_warning"]))
            .Add(new ButtonComponent(_core.Localizer["menu.settings.reset_confirm"], ctx =>
            {
                if (_registry.BySlot(player.Slot) is { } record)
                {
                    _reset.Apply(record);
                    Chat.Send(_core, player, "reset.done");
                }

                // Closed rather than left open: every screen behind this one still shows the old
                // numbers, and staying on a confirm button invites a second press.
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .Build();

    // ---- Help ----------------------------------------------------------------

    private IMenu BuildHelp() =>
        _core.Menu.CreateMenu("ranks.help")
            .WithTitle(_core.Localizer["menu.help.title"])
            .WithItemsPerPage(8)
            .Add(new TextComponent(_core.Localizer["menu.help.points"]))
            .Add(new TextComponent(_core.Localizer["menu.help.ranks"]))
            .Add(new TextComponent(_core.Localizer["menu.help.stats"]))
            .Add(new TextComponent(_core.Localizer["menu.help.scoreboard"]))
            .Add(new TextComponent(_core.Localizer["menu.help.commands"]))
            .Build();
}

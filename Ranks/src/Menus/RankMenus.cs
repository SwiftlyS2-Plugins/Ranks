using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.Menus;

/// <summary>Core's menus. Fixed by design — a module ships its own command and its own menu.</summary>
/// <remarks>
/// Built through <c>Core.Menu</c>, the 1.5.0 framework; the classic <c>Core.MenusAPI</c> surface is
/// <c>[Obsolete]</c>. A player has at most one open menu across every plugin, so opening one of
/// these closes someone else's — that is normal, not an error to guard against. Menus are released
/// automatically when the plugin unloads, so there is no teardown here.
/// </remarks>
internal sealed class RankMenus
{
    private readonly ISwiftlyCore _core;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly PlayerSessions _sessions;
    private readonly ILogger _logger;

    public RankMenus(ISwiftlyCore core, RankLadder ladder, PlayerRegistry registry, PlayerSessions sessions, ILogger logger)
    {
        _core = core;
        _ladder = ladder;
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>Current rank, points and progress to the next band.</summary>
    public void OpenRank(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);

        if (record is null)
        {
            Chat.Send(_core, player, "error.not_loaded");
            return;
        }

        var tier = _ladder.TierFor(record.Points);
        var toNext = _ladder.PointsToNext(record.Points);
        var progress = (float)_ladder.ProgressInBand(record.Points);

        // Ids are namespaced because a menu id doubles as its keybind configuration scope, which
        // keeps the server's generated keybind file readable. Each one names what it shows.
        var menu = _core.Menu.CreateMenu("ranks.myrank")
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
            // Submenus rather than ButtonComponent + Open(): Open() starts a new root menu with no
            // back-stack, so the framework's configured navigation has nothing to go back to.
            .Add(new SubmenuComponent(_core.Localizer["menu.rank.ladder"], _ => BuildLadder(player)))
            .Add(new ButtonComponent(_core.Localizer["menu.rank.stats"], _ => { ShowStats(player); return ValueTask.CompletedTask; }))
            .Build();

        menu.Open(player);
    }

    /// <summary>The whole ladder, marking where the player currently sits.</summary>
    public void OpenLadder(IPlayer player) => BuildLadder(player).Open(player);

    private IMenu BuildLadder(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);
        var index = record is null ? -1 : record.RankIndex;

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

    /// <summary>Preferences. Currently one toggle, which maps 1:1 onto the stored column.</summary>
    public void OpenSettings(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);

        if (record is null)
        {
            Chat.Send(_core, player, "error.not_loaded");
            return;
        }

        _core.Menu.CreateMenu("ranks.settings")
            .WithTitle(_core.Localizer["menu.settings.title"])
            .Add(new ToggleComponent(
                _core.Localizer["menu.settings.XP_messages"],
                record.ShowPointMessages,
                ctx =>
                {
                    record.ShowPointMessages = ctx.NewValue;
                    record.Dirty = true;
                }))
            .Build()
            .Open(player);
    }

    /// <summary>Core's own counters in chat, not a menu — they fit on five lines.</summary>
    public void ShowStats(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);

        if (record is null)
        {
            Chat.Send(_core, player, "error.not_loaded");
            return;
        }

        var ratio = record.Deaths == 0 ? record.Kills : Math.Round(record.Kills / (double)record.Deaths, 2);

        Chat.Send(_core, player, "stats.header");
        Chat.Send(_core, player, "stats.combat", record.Kills, record.Deaths, record.Assists, ratio);
        Chat.Send(_core, player, "stats.rounds", record.RoundWins, record.RoundLosses);
        Chat.Send(_core, player, "stats.matches", record.MatchWins, record.MatchLosses);
        Chat.Send(_core, player, "stats.bombs", record.BombsPlanted, record.BombsDefused);
    }

    /// <summary>The leaderboard. Paging is the component's job, so nothing here hand-rolls it.</summary>
    public void OpenTop(IPlayer player, Func<Task<IReadOnlyList<(int Position, string Name, long Value)>>> loader)
    {
        _core.Menu.CreateMenu("ranks.top")
            .WithTitle(_core.Localizer["menu.top.title"])
            .WithItemsPerPage(10)
            .Add(new PagedCollectionComponent<IMenuComponent>(async () =>
            {
                try
                {
                    var rows = await loader().ConfigureAwait(false);

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
            .Build()
            .Open(player);
    }
}

using Ranks.Prestige.Api;
using Ranks.Prestige.Config;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.Prestige.Menus;

/// <summary>The prestige screen, shown under core's <c>Statistics</c> and by this module's command.</summary>
/// <remarks>
/// Names <c>RanksBridge</c> but no <c>Ranks.Contract</c> type, so it is not a JIT hazard — the
/// bridge is this module's own class and its contract-touching members are isolated behind
/// <c>NoInlining</c>. Every figure is already resident, so there is nothing to load and no paged
/// component here.
/// </remarks>
internal sealed class PrestigeMenus
{
    private readonly ISwiftlyCore _core;
    private readonly RanksBridge _bridge;

    private volatile PrestigeConfig _config;

    public PrestigeMenus(ISwiftlyCore core, RanksBridge bridge, PrestigeConfig config)
    {
        _core = core;
        _bridge = bridge;
        _config = config;
    }

    public void UpdateConfig(PrestigeConfig config) => _config = config;

    public void Open(IPlayer player) => Build(player).Open(player);

    public IMenu Build(IPlayer player)
    {
        var builder = _core.Menu.CreateMenu("prestige.standing")
            .WithTitle(_core.Localizer["menu.prestige.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8);

        if (!_config.Enabled)
            return builder.Add(new TextComponent(_core.Localizer["prestige.disabled"])).AddFooter(new DividerComponent()).Build();

        // IsAvailable is a plain bool, so reading it resolves nothing. Without this the first open
        // on a server with no Ranks installed JITs StandingFor and throws here.
        if (!_bridge.IsAvailable || _bridge.StandingFor(player) is not { } standing)
            return builder.Add(new TextComponent(_core.Localizer["error.unavailable"])).AddFooter(new DividerComponent()).Build();

        builder.Add(new TextComponent(standing.Level == 0
            ? _core.Localizer["prestige.none"]
            : _core.Localizer["prestige.current", standing.Level, standing.DisplayPoints]));

        builder.Add(new TextComponent(_bridge.PointsToNextFor(player) is { } remaining
            ? _core.Localizer["prestige.next", remaining]
            : _core.Localizer["prestige.max"]));

        return builder.AddFooter(new DividerComponent()).Build();
    }
}

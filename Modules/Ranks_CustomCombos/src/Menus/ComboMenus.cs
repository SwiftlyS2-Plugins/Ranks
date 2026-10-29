using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.CustomCombos.Menus;

/// <summary>The combos screen, shown under core's <c>Statistics</c> and by this module's command.</summary>
/// <remarks>
/// Names no <c>Ranks.Contract</c> type, so it is not a JIT hazard on a server without core — the
/// registration that does name one lives in <c>RanksBridge</c>.
/// </remarks>
internal sealed class ComboMenus
{
    private readonly ISwiftlyCore _core;
    private readonly ICustomCombosApi _api;
    private readonly ILogger _logger;

    public ComboMenus(ISwiftlyCore core, ICustomCombosApi api, ILogger logger)
    {
        _core = core;
        _api = api;
        _logger = logger;
    }

    public void Open(IPlayer player) => Build(player).Open(player);

    public IMenu Build(IPlayer player)
    {
        var steamId = player.SteamID;

        return _core.Menu.CreateMenu("customcombos.list")
            .WithTitle(_core.Localizer["menu.combos.title"])
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<IMenuComponent>(async () =>
            {
                try
                {
                    // Already ordered by count, descending.
                    var rows = await _api.GetCombosAsync(steamId).ConfigureAwait(false);

                    return rows.Count == 0
                        ? [new TextComponent(_core.Localizer["menu.combos.empty"])]
                        : rows.Select(r => (IMenuComponent)new TextComponent(
                            _core.Localizer["menu.combos.row", Describe(r.ComboKey), r.Count])).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_CustomCombos: failed to build the combo menu for {SteamId}.", steamId);
                    return [new TextComponent(_core.Localizer["menu.combos.error"])];
                }
            }))
            .Build();
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

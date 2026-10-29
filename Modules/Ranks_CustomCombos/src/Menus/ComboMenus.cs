using System.Text.RegularExpressions;
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

    /// <remarks>
    /// The item type is the row, never <c>IMenuComponent</c>: the component renders each item
    /// through <c>Formatter</c>, whose default is <c>ToString()</c> — so a component as the item
    /// draws its own type name.
    /// </remarks>
    public IMenu Build(IPlayer player)
    {
        var steamId = player.SteamID;

        return _core.Menu.CreateMenu("customcombos.list")
            .WithTitle(_core.Localizer["menu.combos.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<ComboStatRow>(async () =>
            {
                try
                {
                    // Already ordered by count, descending.
                    return await _api.GetCombosAsync(steamId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Rethrown so the component shows FailedText; caught only to get it logged.
                    _logger.LogError(ex, "Ranks_CustomCombos: failed to build the combo menu for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer["menu.combos.row", Describe(r.ComboKey), r.Count],
                EmptyText = _core.Localizer["menu.combos.empty"],
                FailedText = _core.Localizer["menu.combos.error"],
            })
            .AddFooter(new DividerComponent())
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
            : StripColorTokens(resolved);
    }

    /// <remarks>
    /// <c>combo.*</c> names are operator-authored and shared with the arbiter's chat line, so they
    /// routinely carry chat colour tokens. A menu row wants HTML, so a token left in renders as
    /// literal <c>{lime}</c> text to the player.
    /// </remarks>
    private static string StripColorTokens(string text) =>
        ColorToken.Replace(text, string.Empty).Trim();

    private static readonly Regex ColorToken = new(@"\{[a-zA-Z]+\}|\[[a-zA-Z]+\]", RegexOptions.Compiled);
}

using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Config;
using Ranks.WeaponStats.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.WeaponStats.Menus;

/// <summary>This module's two screens, shown under core's <c>Statistics</c> and by its own command.</summary>
/// <remarks>
/// <para>
/// Names no <c>Ranks.Contract</c> type, so it is not a JIT hazard on a server without core and
/// needs none of <c>RanksBridge</c>'s isolation — the registration that does name one lives there.
/// A menu built here is handed to core as a factory result and adopted by the entry that opened
/// it, which is what makes the player's back key return to core's menu.
/// </para>
/// <para>
/// Every figure comes from <see cref="IWeaponStatsApi"/> rather than the writer, because that is
/// the one source that folds in the unflushed session delta — a menu reading the database directly
/// would show a player nothing for the round they are in the middle of.
/// </para>
/// </remarks>
internal sealed class WeaponStatsMenus
{
    private readonly ISwiftlyCore _core;
    private readonly IWeaponStatsApi _api;
    private readonly ILogger _logger;

    private volatile WeaponStatsConfig _config;

    public WeaponStatsMenus(ISwiftlyCore core, IWeaponStatsApi api, WeaponStatsConfig config, ILogger logger)
    {
        _core = core;
        _api = api;
        _config = config;
        _logger = logger;
    }

    public void UpdateConfig(WeaponStatsConfig config) => _config = config;

    public void OpenWeapons(IPlayer player) => BuildWeapons(player).Open(player);

    /// <summary>Per-weapon kills, accuracy and headshot rate, best first.</summary>
    /// <remarks>
    /// The item type is the row, never <c>IMenuComponent</c>: the component renders each item
    /// through <c>Formatter</c>, whose default is <c>ToString()</c> — so a component as the item
    /// draws its own type name.
    /// </remarks>
    public IMenu BuildWeapons(IPlayer player)
    {
        var steamId = player.SteamID;
        var minShots = _config.Display.MinShotsToList;

        return _core.Menu.CreateMenu("weaponstats.weapons")
            .WithTitle(_core.Localizer["menu.weapons.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<WeaponStatRow>(async () =>
            {
                try
                {
                    var rows = await _api.GetWeaponsAsync(steamId).ConfigureAwait(false);

                    // The operator's noise floor still applies: a menu page of weapons fired once
                    // each buries the ones the player actually uses. There is no row cap, though —
                    // paging is what the old TopWeapons limit existed to stand in for.
                    return rows
                        .Where(r => r.Shots >= minShots)
                        .OrderByDescending(r => r.Kills)
                        .ThenByDescending(r => r.Damage)
                        .ToList();
                }
                catch (Exception ex)
                {
                    // Rethrown so the component shows FailedText; caught only to get it logged.
                    _logger.LogError(ex, "Ranks_WeaponStats: failed to build the weapon menu for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer[
                    "menu.weapons.row",
                    Display(r.Weapon),
                    r.Kills,
                    Percent(r.Headshots, r.Kills),
                    Percent(r.Hits, r.Hits + r.Misses)],
                ItemHint = _ => _core.Localizer["menu.weapons.hint"],
                OnItemSelected = ctx =>
                {
                    var child = BuildWeaponHitGroups(ctx.Player, ctx.Item.Weapon);

                    // Set explicitly: adoption only happens for a menu a SubmenuComponent opens,
                    // and this one is opened from a row handler, so without it the back key would
                    // close the tree instead of returning to the weapon list.
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer["menu.weapons.empty"],
                FailedText = _core.Localizer["menu.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();
    }

    /// <summary>Where every landed hit went, across all weapons.</summary>
    public IMenu BuildHitGroups(IPlayer player) =>
        BuildHitGroupMenu(
            "weaponstats.hitgroups",
            _core.Localizer["menu.hitgroups.title"],
            player.SteamID,
            weapon: null);

    /// <summary>The same breakdown for one weapon, opened from a row of the weapon list.</summary>
    private IMenu BuildWeaponHitGroups(IPlayer player, string weapon) =>
        BuildHitGroupMenu(
            // Per-weapon id so a server owner can rebind or restyle the drill-down on its own;
            // the weapon is not in the id, or every gun would be a separate config scope.
            "weaponstats.weaponhitgroups",
            _core.Localizer["menu.weaponhits.title", Display(weapon)],
            player.SteamID,
            weapon);

    private IMenu BuildHitGroupMenu(string menuId, string title, ulong steamId, string? weapon) =>
        _core.Menu.CreateMenu(menuId)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<HitRow>(async () =>
            {
                try
                {
                    // One weapon's rows come from the per-weapon table rather than by filtering
                    // the overall one, which carries no weapon to filter on.
                    var rows = weapon is null
                        ? (await _api.GetHitGroupsAsync(steamId).ConfigureAwait(false))
                            .Select(r => (r.HitGroup, r.Hits, r.Damage))
                        : (await _api.GetWeaponHitGroupsAsync(steamId).ConfigureAwait(false))
                            .Where(r => string.Equals(r.Weapon, weapon, StringComparison.OrdinalIgnoreCase))
                            .Select(r => (r.HitGroup, r.Hits, r.Damage));

                    var listed = rows.ToList();

                    // Share is projected here rather than formatted later: the formatter sees one
                    // row at a time and cannot know the total.
                    var total = listed.Sum(r => r.Hits);

                    return listed
                        .OrderByDescending(r => r.Hits)
                        .Select(r => new HitRow(HitGroupName(r.HitGroup), r.Hits, Percent(r.Hits, total), r.Damage))
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_WeaponStats: failed to build the hitgroup menu for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer["menu.hitgroups.row", r.Name, r.Hits, r.Share, r.Damage],
                EmptyText = _core.Localizer["menu.hitgroups.empty"],
                FailedText = _core.Localizer["menu.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    private readonly record struct HitRow(string Name, long Hits, double Share, long Damage);

    private static double Percent(long part, long whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    /// <remarks>
    /// Strips the stored designer-name prefix and upper-cases for display only. The stored form
    /// stays <c>weapon_ak47</c>, which is what the entity system, the dumps and weapon_fire all
    /// agree on — so it is also what the per-weapon lookup matches on.
    /// </remarks>
    private static string Display(string weapon) =>
        (weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon[7..] : weapon).ToUpperInvariant();

    /// <remarks>
    /// Mirrors <c>HitGroup_t</c>, which is not a dense range — 9 is <c>UNUSED</c> and never
    /// appears. An unrecognised value is shown as its number rather than dropped, so a future
    /// engine addition surfaces as an odd row instead of silently vanishing from the totals.
    /// </remarks>
    private string HitGroupName(short hitGroup) => hitGroup switch
    {
        0 => _core.Localizer["hitgroup.generic"],
        1 => _core.Localizer["hitgroup.head"],
        2 => _core.Localizer["hitgroup.chest"],
        3 => _core.Localizer["hitgroup.stomach"],
        4 => _core.Localizer["hitgroup.left_arm"],
        5 => _core.Localizer["hitgroup.right_arm"],
        6 => _core.Localizer["hitgroup.left_leg"],
        7 => _core.Localizer["hitgroup.right_leg"],
        8 => _core.Localizer["hitgroup.neck"],
        10 => _core.Localizer["hitgroup.gear"],
        11 => _core.Localizer["hitgroup.special"],
        _ => _core.Localizer["hitgroup.unknown", hitGroup],
    };
}

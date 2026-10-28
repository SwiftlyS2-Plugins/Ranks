namespace Ranks.WeaponStats.Contract;

/// <summary>One player's lifetime totals for a single weapon.</summary>
/// <remarks>
/// <paramref name="Weapon"/> is the entity designer name — <c>weapon_ak47</c>, not <c>ak47</c>.
/// Death sources that are not weapons keep their bare name (<c>world</c>, <c>planted_c4</c>), and
/// a value the module could not use at all becomes <c>unknown</c>.
///
/// <paramref name="Misses"/> counts trigger pulls that damaged nobody, which is <em>not</em>
/// <c>Shots - Hits</c>: one shotgun blast is a single shot that can land nine hits, so that
/// subtraction goes negative. Weapons that cannot deal damage at all — smokes, flashes, decoys —
/// therefore report every shot as a miss.
/// </remarks>
public sealed record WeaponStatRow(
    string Weapon,
    long Shots,
    long Hits,
    long Misses,
    long Damage,
    long Kills,
    long Deaths,
    long Headshots);

/// <summary>The hitgroup values this module stores beyond the engine's own.</summary>
/// <remarks>
/// <c>HitGroup_t</c> has no negative members, so <see cref="Miss"/> cannot collide with a real one.
/// </remarks>
public static class WeaponHitGroups
{
    /// <summary>Trigger pulls that damaged nobody. <c>Damage</c> on such a row is always 0.</summary>
    /// <remarks>
    /// A bucket rather than a column because the row is keyed by hitgroup and a miss has none. It
    /// is what makes the distribution cover every trigger pull instead of only the ones that
    /// landed — but note the shotgun caveat on <see cref="WeaponStatRow"/>: hits plus misses is
    /// still not shots.
    /// </remarks>
    public const short Miss = -1;
}

/// <summary>One player's lifetime totals for a single hitgroup, from the attacker's side.</summary>
/// <remarks>
/// <paramref name="HitGroup"/> is the raw <c>HitGroup_t</c> value, which is not a dense range:
/// <c>GENERIC = 0</c> through <c>SPECIAL = 11</c> with a hole at <c>UNUSED = 9</c>, plus
/// <see cref="WeaponHitGroups.Miss"/>. Nothing records where a player <em>gets</em> hit, so there
/// is no victim-side counterpart.
/// </remarks>
public sealed record HitGroupStatRow(
    short HitGroup,
    long Hits,
    long Damage);

/// <summary>Where one weapon's shots landed, for a single hitgroup.</summary>
/// <remarks>
/// The same rows as <see cref="HitGroupStatRow"/> split by weapon — summing these over weapons
/// reproduces it exactly, miss bucket included.
/// </remarks>
public sealed record WeaponHitGroupStatRow(
    string Weapon,
    short HitGroup,
    long Hits,
    long Damage);

/// <summary>Read access to <c>Ranks_WeaponStats</c>'s per-weapon and per-hitbox totals.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
///
/// Every method is async because, unlike core's resident player record, these totals live in the
/// database — and every one of them includes the caller's unflushed session delta, so a figure
/// read here always matches what <c>!weaponstats</c> would print.
/// </remarks>
public interface IWeaponStatsApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.WeaponStats.Api.v1";

    /// <summary>Every weapon the player has used, unordered.</summary>
    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId);

    /// <summary>One row per hitgroup the player has landed a hit on, unordered.</summary>
    /// <remarks>Includes the <see cref="WeaponHitGroups.Miss"/> bucket.</remarks>
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId);

    /// <summary>One row per weapon and hitgroup the player has used, unordered.</summary>
    /// <remarks>
    /// Includes the <see cref="WeaponHitGroups.Miss"/> bucket per weapon, so a caller can render
    /// where a single gun's shots go without a second query.
    /// </remarks>
    Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId);

    /// <summary>A single weapon's totals, or null when the player has never used it.</summary>
    /// <remarks>Spares a caller rendering one weapon the cost of fetching the whole table.</remarks>
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon);
}

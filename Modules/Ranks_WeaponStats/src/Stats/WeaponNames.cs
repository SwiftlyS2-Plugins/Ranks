namespace Ranks.WeaponStats.Stats;

/// <summary>Normalises the weapon string so one gun lands in one row.</summary>
/// <remarks>
/// The two events disagree: <c>weapon_fire</c> carries the entity designer name
/// (<c>weapon_ak47</c>) and <c>player_death</c> the short form (<c>ak47</c>). Stored verbatim,
/// every gun gets two rows, shots never line up with kills, and accuracy is permanently wrong
/// with nothing in the data hinting why.
/// </remarks>
internal static class WeaponNames
{
    private const int MaxLength = 64;

    /// <remarks>Carries no prefix — it is not a weapon, so it cannot collide with one.</remarks>
    public const string Unknown = "unknown";

    /// <summary>Death sources that are not weapon entities and must never be given the prefix.</summary>
    /// <remarks>
    /// There is no <c>weapon_world</c>. These only ever arrive from player_death / player_hurt,
    /// never from weapon_fire, so they cannot collide with a real weapon's row. An incomplete list
    /// here is cosmetic rather than corrupting: a missing source is stored as
    /// <c>weapon_&lt;source&gt;</c>, an ugly label on its own single row, and no weapon's counters
    /// are split or merged by it.
    /// </remarks>
    private static readonly HashSet<string> NonWeaponSources = new(StringComparer.Ordinal)
    {
        "world",
        "planted_c4",
        "inferno",
        "trigger_hurt",
    };

    public static string Normalize(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return Unknown;

        var name = weapon.Trim();

        if (!name.StartsWith("weapon_", StringComparison.Ordinal) && !NonWeaponSources.Contains(name))
            name = "weapon_" + name;

        // Checked after prefixing, not before. An over-length value becomes "unknown" rather than
        // being cut, because truncating would merge two weapons into one row.
        return name.Length <= MaxLength ? name : Unknown;
    }
}

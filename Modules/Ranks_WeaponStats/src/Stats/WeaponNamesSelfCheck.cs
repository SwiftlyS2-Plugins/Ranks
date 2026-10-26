namespace Ranks.WeaponStats.Stats;

/// <summary>Asserts that the two events agree on a weapon's name.</summary>
/// <remarks>
/// This is the module's central correctness property and it fails silently: store the two event
/// spellings verbatim and every gun gets two rows, shots never line up with kills, and accuracy is
/// permanently wrong with nothing in the data hinting why. A handful of string comparisons at
/// load, and the only check this logic gets.
/// </remarks>
internal static class WeaponNamesSelfCheck
{
    public static void Run()
    {
        BothEventSpellingsLandOnOneRow();
        NonWeaponSourcesKeepTheirBareName();
        UnusableValuesBecomeUnknown();
    }

    /// <remarks>weapon_fire reports the designer name, player_death the short form.</remarks>
    private static void BothEventSpellingsLandOnOneRow()
    {
        foreach (var (shortForm, designerName) in (ReadOnlySpan<(string, string)>)[("ak47", "weapon_ak47"), ("awp", "weapon_awp"), ("knife_t", "weapon_knife_t")])
        {
            var fromDeath = WeaponNames.Normalize(shortForm);
            var fromFire = WeaponNames.Normalize(designerName);

            Require(fromDeath == designerName, $"'{shortForm}' normalised to '{fromDeath}', expected '{designerName}'");
            Require(fromFire == designerName, $"'{designerName}' normalised to '{fromFire}', expected '{designerName}'");
        }
    }

    /// <remarks>There is no weapon_world; prefixing a death source would invent one.</remarks>
    private static void NonWeaponSourcesKeepTheirBareName()
    {
        foreach (var source in (ReadOnlySpan<string>)["world", "planted_c4", "inferno", "trigger_hurt"])
        {
            var normalised = WeaponNames.Normalize(source);

            Require(normalised == source, $"'{source}' normalised to '{normalised}', expected it unchanged");
        }
    }

    /// <remarks>
    /// An over-length value becomes "unknown" rather than being cut: truncating would merge two
    /// weapons into one row, which is worse than labelling one badly.
    /// </remarks>
    private static void UnusableValuesBecomeUnknown()
    {
        foreach (var value in (ReadOnlySpan<string?>)[null, "", "   ", new string('x', 120)])
        {
            var normalised = WeaponNames.Normalize(value);

            Require(normalised == WeaponNames.Unknown, $"an unusable value normalised to '{normalised}', expected '{WeaponNames.Unknown}'");
        }

        Require(!WeaponNames.Unknown.StartsWith("weapon_", StringComparison.Ordinal),
            "the unknown placeholder carries a weapon_ prefix and could collide with a real weapon");
    }

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks_WeaponStats: weapon-name self-check failed — {failure}.");
    }
}

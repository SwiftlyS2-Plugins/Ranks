namespace Ranks.CustomCombos.Combos;

/// <summary>Maps a weapon classname to a broad class, so a combo can say "any sniper".</summary>
/// <remarks>
/// This module's own mapping, not core's. Core classifies only the five kinds it prices
/// (knife / taser / grenade / inferno / impact); the ten here exist for combos, and a weapon with
/// no entry simply matches no <c>weapon_class:</c> test rather than falling into a bucket.
/// </remarks>
internal static class WeaponClasses
{
    public const string Knife = "knife";
    public const string Grenade = "grenade";
    public const string Inferno = "inferno";
    public const string Taser = "taser";
    public const string Pistol = "pistol";
    public const string Smg = "smg";
    public const string Rifle = "rifle";
    public const string Sniper = "sniper";
    public const string Shotgun = "shotgun";
    public const string Heavy = "heavy";

    public static readonly IReadOnlyList<string> All =
        [Knife, Grenade, Inferno, Taser, Pistol, Smg, Rifle, Sniper, Shotgun, Heavy];

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["taser"] = Taser,
        ["hegrenade"] = Grenade,
        ["inferno"] = Inferno, ["molotov"] = Inferno, ["incgrenade"] = Inferno,

        ["glock"] = Pistol, ["usp_silencer"] = Pistol, ["hkp2000"] = Pistol, ["p250"] = Pistol,
        ["fiveseven"] = Pistol, ["tec9"] = Pistol, ["cz75a"] = Pistol, ["deagle"] = Pistol,
        ["revolver"] = Pistol, ["elite"] = Pistol,

        ["mac10"] = Smg, ["mp9"] = Smg, ["mp7"] = Smg, ["mp5sd"] = Smg,
        ["ump45"] = Smg, ["p90"] = Smg, ["bizon"] = Smg,

        ["galilar"] = Rifle, ["famas"] = Rifle, ["ak47"] = Rifle, ["m4a1"] = Rifle,
        ["m4a1_silencer"] = Rifle, ["sg556"] = Rifle, ["aug"] = Rifle,

        ["awp"] = Sniper, ["ssg08"] = Sniper, ["scar20"] = Sniper, ["g3sg1"] = Sniper,

        ["nova"] = Shotgun, ["xm1014"] = Shotgun, ["mag7"] = Shotgun, ["sawedoff"] = Shotgun,

        ["m249"] = Heavy, ["negev"] = Heavy,
    };

    /// <summary>The class of a weapon, or null when it belongs to none.</summary>
    public static string? Of(string weapon)
    {
        if (string.IsNullOrEmpty(weapon)) return null;

        // The event sometimes carries the entity designer name and sometimes the bare one; core
        // normalises neither, so both spellings have to resolve to the same class here.
        var bare = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? weapon["weapon_".Length..]
            : weapon;

        if (Map.TryGetValue(bare, out var mapped)) return mapped;

        // Every knife skin is its own classname, so the family is matched rather than listed.
        return bare.Contains("knife", StringComparison.OrdinalIgnoreCase) || bare.Equals("bayonet", StringComparison.OrdinalIgnoreCase)
            ? Knife
            : null;
    }
}

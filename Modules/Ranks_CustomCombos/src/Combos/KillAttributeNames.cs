namespace Ranks.CustomCombos.Combos;

/// <summary>The flag attributes core puts in a kill's attribute set.</summary>
/// <remarks>
/// A deliberate mirror of <c>Ranks.Contract.RanksKillAttributes</c> rather than a reference to it.
/// Only the bridge file may name a contract type — a field or constant of one here would make this
/// class a JIT hazard on a server with no Ranks installed, which fails the <em>whole</em> host
/// rather than this module.
///
/// Used only to validate configured names at load, so core gaining a flag this list does not know
/// degrades to "that attribute is rejected with a named reason", not to silent mismatching. Move
/// the two together.
/// </remarks>
internal static class KillAttributeNames
{
    public static readonly IReadOnlyList<string> All =
    [
        "headshot",
        "noscope",
        "thrusmoke",
        "attackerblind",
        "attackerinair",
        "assistedflash",
        "penetrated",
        "dominated",
        "revenge",
        "team_kill",
    ];
}

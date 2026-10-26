namespace Ranks.Contract;

/// <summary>The facts core derived about one kill, handed to the arbiter (FR-API-11).</summary>
/// <remarks>
/// Flag-shaped facts arrive in <see cref="Attributes"/>; valued ones stay as fields because an
/// arbiter matching "past 2000 units" or "hitgroup 5" needs the number, and core cannot pre-expand
/// a set entry for every threshold somebody might configure.
/// </remarks>
public sealed record RanksKillContext(
    ulong AttackerSteamId,
    ulong? VictimSteamId,
    bool VictimIsBot,
    bool FriendlyFire,
    string Weapon,
    float Distance,
    int HitGroup,
    IReadOnlySet<string> Attributes);

/// <summary>An arbiter's answer: award this instead of, or alongside, core's own kill awards.</summary>
/// <remarks>
/// <paramref name="Reason"/> is a stable key, never prose — it is written to the detail log and
/// carried on <c>PointsChanged</c>, so translated text there would make a module-facing identity
/// change with the server's language. <paramref name="DisplayText"/> carries the words.
/// </remarks>
public sealed record RanksKillClaim(
    string Reason,
    int Points,
    string? DisplayText = null,
    bool SuppressCoreAwards = false);

/// <summary>Consulted by core before it awards a kill (FR-API-11).</summary>
/// <remarks>
/// Core asks *before* awarding rather than letting a module react afterwards, which is the only
/// ordering under which suppression is expressible: a module hooking <c>player_death</c> itself
/// cannot stop core, and plugin order is not guaranteed.
///
/// Runs on the game thread inside core's award path. Returning fast matters, and touching the
/// database here does not.
/// </remarks>
public interface IRanksKillArbiter
{
    /// <summary>The claim for this kill, or null to leave it entirely to core.</summary>
    RanksKillClaim? ClaimKill(RanksKillContext context);
}

/// <summary>The flag attributes core puts in <see cref="RanksKillContext.Attributes"/>.</summary>
/// <remarks>
/// Named for the <c>player_death</c> field each one reads, so an operator can check a name against
/// an event dump rather than against documentation. Shared rather than duplicated per side: core
/// writes these strings and an arbiter matches them, and a typo in either is silent.
/// </remarks>
public static class RanksKillAttributes
{
    public const string Headshot = "headshot";
    public const string NoScope = "noscope";
    public const string ThruSmoke = "thrusmoke";
    public const string AttackerBlind = "attackerblind";
    public const string AttackerInAir = "attackerinair";
    public const string AssistedFlash = "assistedflash";

    /// <remarks>The field is a count, not a flag; present when it is above zero.</remarks>
    public const string Penetrated = "penetrated";

    /// <inheritdoc cref="Penetrated"/>
    public const string Dominated = "dominated";

    /// <inheritdoc cref="Penetrated"/>
    public const string Revenge = "revenge";

    /// <summary>Attacker and victim on the same team.</summary>
    public const string TeamKill = "team_kill";

    /// <summary>Every flag core can emit, for an arbiter validating configured names at load.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Headshot, NoScope, ThruSmoke, AttackerBlind, AttackerInAir,
        AssistedFlash, Penetrated, Dominated, Revenge, TeamKill,
    ];
}

using System.ComponentModel;

namespace Ranks.Config;

/// <summary>Core's closed event catalogue (FR-PTS-06) and killstreak tiers (FR-PTS-07).</summary>
/// <remarks>
/// A dictionary rather than named properties because the binder merges dictionaries by key: the
/// shipped defaults survive for keys the operator did not touch, and a key they did touch wins.
/// Values only — the two tuning constants that live beside these upstream (<c>LongDistance</c>,
/// <c>SecondsBetweenKills</c>) are in config.jsonc, so every key here reads as "worth this many
/// points" with no exceptions to remember.
/// </remarks>
internal sealed class PointsConfig
{
    [Description("Point value per event. Set a value to 0 to disable that event's award and its chat line; the stat counter still increments.")]
    public Dictionary<string, int> Values { get; set; } = new(PointEvents.Defaults, StringComparer.OrdinalIgnoreCase);

    public int Value(string eventKey) => Values.GetValueOrDefault(eventKey, 0);
}

/// <summary>Core's event keys and their shipped values.</summary>
internal static class PointEvents
{
    public const string Kill = "core.kill";
    public const string Death = "core.death";
    public const string Assist = "core.assist";
    public const string AssistFlash = "core.assist_flash";
    public const string Headshot = "core.headshot";
    public const string Suicide = "core.suicide";
    public const string TeamKill = "core.team_kill";
    public const string TeamAssist = "core.team_assist";
    public const string NoScope = "core.noscope";
    public const string Wallbang = "core.wallbang";
    public const string ThroughSmoke = "core.through_smoke";
    public const string BlindKill = "core.blind_kill";
    public const string LongDistance = "core.long_distance";
    public const string KnifeKill = "core.knife_kill";
    public const string TaserKill = "core.taser_kill";
    public const string GrenadeKill = "core.grenade_kill";
    public const string InfernoKill = "core.inferno_kill";
    public const string ImpactKill = "core.impact_kill";
    public const string BombPlant = "core.bomb_plant";
    public const string BombDefuse = "core.bomb_defuse";
    public const string BombDefuseOthers = "core.bomb_defuse_others";
    public const string BombExploded = "core.bomb_exploded";
    public const string BombPickup = "core.bomb_pickup";
    public const string BombDrop = "core.bomb_drop";
    public const string HostageRescue = "core.hostage_rescue";
    public const string HostageRescueAll = "core.hostage_rescue_all";
    public const string HostageHurt = "core.hostage_hurt";
    public const string HostageKill = "core.hostage_kill";
    public const string RoundWin = "core.round_win";
    public const string RoundLose = "core.round_lose";
    public const string Mvp = "core.mvp";
    public const string MatchWin = "core.match_win";
    public const string MatchLose = "core.match_lose";

    /// <summary>Killstreak tiers, indexed by kill count from 2 upward; 12+ all pay the last entry.</summary>
    public static readonly (int Kills, string Key)[] Streaks =
    [
        (2, "core.streak_double"),
        (3, "core.streak_triple"),
        (4, "core.streak_domination"),
        (5, "core.streak_rampage"),
        (6, "core.streak_mega"),
        (7, "core.streak_ownage"),
        (8, "core.streak_ultra"),
        (9, "core.streak_killing_spree"),
        (10, "core.streak_monster"),
        (11, "core.streak_unstoppable"),
        (12, "core.streak_godlike"),
    ];

    /// <remarks>
    /// Derived from the K4-LevelRanks defaults, which are a known quantity on live servers, with
    /// three deliberate departures: hostage_kill is a penalty rather than the upstream's reward at
    /// double the value of a rescue; match_win / match_lose have no upstream counterpart because
    /// LVL Ranks models no match; and playtime rewards are absent, since core prices actions rather
    /// than occupying a slot.
    /// </remarks>
    public static readonly Dictionary<string, int> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        [Kill] = 2,
        [Death] = -3,
        [Assist] = 1,
        [AssistFlash] = 1,
        [Headshot] = 3,
        [Suicide] = 0,
        [TeamKill] = 0,
        [TeamAssist] = 0,
        [NoScope] = 5,
        [Wallbang] = 4,
        [ThroughSmoke] = 3,
        [BlindKill] = 3,
        [LongDistance] = 3,
        [KnifeKill] = 5,
        [TaserKill] = 5,
        [GrenadeKill] = 5,
        [InfernoKill] = 5,
        [ImpactKill] = 5,
        [BombPlant] = 10,
        [BombDefuse] = 10,
        [BombDefuseOthers] = 10,
        [BombExploded] = 10,
        [BombPickup] = 2,
        [BombDrop] = -2,
        [HostageRescue] = 10,
        [HostageRescueAll] = 10,
        [HostageHurt] = 0,
        [HostageKill] = -20,
        [RoundWin] = 2,
        [RoundLose] = -2,
        [Mvp] = 10,
        [MatchWin] = 15,
        [MatchLose] = -5,
        ["core.streak_double"] = 5,
        ["core.streak_triple"] = 6,
        ["core.streak_domination"] = 7,
        ["core.streak_rampage"] = 8,
        ["core.streak_mega"] = 9,
        ["core.streak_ownage"] = 10,
        ["core.streak_ultra"] = 11,
        ["core.streak_killing_spree"] = 12,
        ["core.streak_monster"] = 13,
        ["core.streak_unstoppable"] = 14,
        ["core.streak_godlike"] = 15,
    };
}

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
    public const string Kill = "kill";
    public const string Death = "death";
    public const string Assist = "assist";
    public const string AssistFlash = "assist_flash";
    public const string Headshot = "headshot";
    public const string Suicide = "suicide";
    public const string TeamKill = "team_kill";
    public const string TeamAssist = "team_assist";
    public const string NoScope = "noscope";
    public const string Wallbang = "wallbang";
    public const string ThroughSmoke = "through_smoke";
    public const string BlindKill = "blind_kill";
    public const string LongDistance = "long_distance";
    public const string KnifeKill = "knife_kill";
    public const string TaserKill = "taser_kill";
    public const string GrenadeKill = "grenade_kill";
    public const string InfernoKill = "inferno_kill";
    public const string ImpactKill = "impact_kill";
    public const string BombPlant = "bomb_plant";
    public const string BombDefuse = "bomb_defuse";
    public const string BombDefuseOthers = "bomb_defuse_others";
    public const string BombExploded = "bomb_exploded";
    public const string BombPickup = "bomb_pickup";
    public const string BombDrop = "bomb_drop";
    public const string HostageRescue = "hostage_rescue";
    public const string HostageRescueAll = "hostage_rescue_all";
    public const string HostageHurt = "hostage_hurt";
    public const string HostageKill = "hostage_kill";
    public const string RoundWin = "round_win";
    public const string RoundLose = "round_lose";
    public const string Mvp = "mvp";
    public const string MatchWin = "match_win";
    public const string MatchLose = "match_lose";

    /// <summary>Killstreak tiers, indexed by kill count from 2 upward; 12+ all pay the last entry.</summary>
    public static readonly (int Kills, string Key)[] Streaks =
    [
        (2, "streak.double"),
        (3, "streak.triple"),
        (4, "streak.domination"),
        (5, "streak.rampage"),
        (6, "streak.mega"),
        (7, "streak.ownage"),
        (8, "streak.ultra"),
        (9, "streak.killing_spree"),
        (10, "streak.monster"),
        (11, "streak.unstoppable"),
        (12, "streak.godlike"),
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
        ["streak.double"] = 5,
        ["streak.triple"] = 6,
        ["streak.domination"] = 7,
        ["streak.rampage"] = 8,
        ["streak.mega"] = 9,
        ["streak.ownage"] = 10,
        ["streak.ultra"] = 11,
        ["streak.killing_spree"] = 12,
        ["streak.monster"] = 13,
        ["streak.unstoppable"] = 14,
        ["streak.godlike"] = 15,
    };
}

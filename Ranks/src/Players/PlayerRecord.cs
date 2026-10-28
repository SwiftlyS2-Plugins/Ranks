using Ranks.Contract;

namespace Ranks.Players;

/// <summary>One player's resident record. Mutated on the game thread, snapshotted for writes.</summary>
internal sealed class PlayerRecord
{
    public required ulong SteamId { get; init; }

    /// <remarks>
    /// Rewritten on every save rather than fixed at first sight: <c>!top</c> renders offline players
    /// from this column, so a stale value makes the ladder look broken to the person most likely to
    /// notice. It is also the only column sourced from the player rather than from play, which is
    /// why the writer isolates each row behind a savepoint.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    public int Points { get; set; }

    public int Kills { get; set; }

    public int Deaths { get; set; }

    public int Assists { get; set; }

    /// <summary>Enemy kills that were headshots.</summary>
    public int Headshots { get; set; }

    /// <summary>Shots that damaged someone. One blast can land several, so this is not shots.</summary>
    public int Hits { get; set; }

    /// <summary>Trigger pulls that damaged nobody. Never <c>shots - hits</c>; see <see cref="PendingShot"/>.</summary>
    public int Misses { get; set; }

    public int RoundWins { get; set; }

    public int RoundLosses { get; set; }

    public int MatchWins { get; set; }

    public int MatchLosses { get; set; }

    public int BombsPlanted { get; set; }

    public int BombsDefused { get; set; }

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>Per-player override of <c>Points.ShowPointMessages</c>, from <c>ranks_settings</c>.</summary>
    public bool ShowPointMessages { get; set; } = true;

    /// <summary>Which ladder tier the player is in, 0-based.</summary>
    public int RankIndex { get; set; }

    /// <summary>Kills this round, for the killstreak tiers.</summary>
    public int Killstreak { get; set; }

    /// <summary>When the last kill landed, for <c>Points.SecondsBetweenKills</c>.</summary>
    public DateTime LastKillAt { get; set; }

    /// <summary>Session-only scoreboard overlay (FR-API-07); never persisted.</summary>
    public RanksDisplayOverride? DisplayOverride { get; set; }

    /// <summary>Buffered point lines awaiting the round-end summary.</summary>
    public int PendingSummaryPoints { get; set; }

    /// <summary>A fired shot not yet seen to damage anyone; never persisted.</summary>
    /// <remarks>
    /// Resolved into <see cref="Misses"/> by the next shot. Player-scoped rather than per weapon,
    /// so a grenade whose damage lands after the next rifle shot is attributed to that shot —
    /// Ranks_WeaponStats latches per weapon because it reports per weapon, and this does not.
    /// </remarks>
    public bool PendingShot { get; set; }

    public bool Dirty { get; set; }
}

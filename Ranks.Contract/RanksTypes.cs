namespace Ranks.Contract;

/// <summary>An immutable view of a player's record, taken at the moment of the call.</summary>
/// <remarks>
/// Carries no leaderboard position: everything here is already resident, so building one is
/// allocation-only, while "47th of 3812" needs a query — <c>GetPositionAsync</c> (FR-API-02).
/// </remarks>
public sealed record RanksPlayerSnapshot(
    ulong SteamId,
    string Name,
    int Points,
    string RankName,
    string RankTag,
    int RankIndex,
    long Kills,
    long Deaths,
    long Assists,
    long RoundWins,
    long RoundLosses,
    long MatchWins,
    long MatchLosses,
    long BombsPlanted,
    long BombsDefused,
    DateTime FirstSeen,
    DateTime LastSeen);

/// <summary>One row of a leaderboard query.</summary>
public sealed record RanksLeaderboardEntry(
    int Position,
    ulong SteamId,
    string Name,
    long Value,
    string RankName,
    string RankTag);

/// <summary>Raised after a point change has been applied.</summary>
public sealed record RanksPointsChangedArgs(
    ulong SteamId,
    int OldPoints,
    int NewPoints,
    int Delta,
    string Reason);

/// <summary>Raised when a player crosses a ladder threshold in either direction.</summary>
public sealed record RanksRankChangedArgs(
    ulong SteamId,
    int Points,
    string OldRankName,
    string NewRankName,
    int OldRankIndex,
    int NewRankIndex,
    bool Promoted);

/// <summary>A mutable award, raised before core writes it.</summary>
/// <remarks>
/// A class rather than a record struct because subscribers mutate it — a struct would be copied
/// per subscriber and every change discarded. Fires for core's own awards only, never for
/// <c>AddPoints</c> / <c>SetPoints</c> (FR-API-04).
/// </remarks>
public sealed class RanksPointsChangingArgs
{
    public required ulong SteamId { get; init; }

    /// <summary>Core's event key, e.g. <c>core.death</c>.</summary>
    public required string EventKey { get; init; }

    /// <summary>Attacker on a death, victim on a kill; null when there is no counterparty.</summary>
    public required ulong? CounterpartySteamId { get; init; }

    /// <summary>Configured value multiplied by occurrence count, before any multiplier.</summary>
    public required int BaseAmount { get; init; }

    /// <remarks>Subscribers run in subscription order and each sees the previous one's value.</remarks>
    public int Amount { get; set; }

    /// <summary>Suppresses the write entirely; <c>PointsChanged</c> does not then fire.</summary>
    public bool Cancel { get; set; }
}

/// <summary>A snapshot of every point-related setting the operator configured.</summary>
/// <remarks>
/// Readable in both modes — with <c>UseCoreRankingLogic</c> off core applies none of these itself,
/// so a rating module reads them as the operator's intent rather than as core's behaviour
/// (FR-API-05).
/// </remarks>
public sealed record RanksPointSettings(
    bool UseCoreRankingLogic,
    int MinPlayers,
    bool WarmupPoints,
    bool PointsForBots,
    bool FfaMode,
    double VipMultiplier,
    IReadOnlyList<string> VipFlags,
    bool DynamicKillPoints,
    double DynamicKillMin,
    double DynamicKillMax,
    bool DynamicDeathPoints,
    double DynamicDeathMin,
    double DynamicDeathMax,
    int StartPoints,
    int MinPoints,
    IReadOnlyDictionary<string, int> EventValues);

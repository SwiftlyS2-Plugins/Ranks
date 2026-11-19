namespace Ranks.EloPoints.Api;

/// <summary>The operator intent this module reads from core rather than configuring twice.</summary>
/// <remarks>
/// A module-owned record rather than core's <c>RanksPointSettings</c>: returning a contract type
/// would put it in a signature outside <see cref="RanksBridge"/>, which is the same JIT hazard the
/// <c>object?</c> field exists to prevent, one indirection further out.
///
/// Core applies none of these itself while it is not scoring (FR-PTS-00); they are still what
/// the operator asked for, and FR-MOD-05 rule 3 says a rating module honours them.
/// </remarks>
internal readonly record struct CoreSettings(
    bool UseCoreRankingLogic,
    int StartPoints,
    int MinPoints,
    int MinPlayers,
    bool WarmupPoints,
    bool BotsEnabled);

/// <summary>An admin's change to a player's points, as core reported it.</summary>
/// <remarks>Module-owned for the same reason as <see cref="CoreSettings"/>: it carries core's event past the bridge.</remarks>
/// <param name="Absolute">True for <c>!setpoints</c>, which names the new total rather than a change to it.</param>
internal readonly record struct AdminPointChange(ulong SteamId, int OldPoints, int NewPoints, int Delta, bool Absolute);

/// <summary>Where this module stands on core's scoring claim (FR-API-14).</summary>
internal enum ScoringClaim
{
    /// <summary>Not asked yet.</summary>
    None,

    /// <summary>This module owns scoring, so core awards nothing of its own.</summary>
    Held,

    /// <summary>Another module owns it.</summary>
    Refused,

    /// <summary>Ranks predates the claim, so <c>Rank.UseCoreRankingLogic</c> alone decides whether core scores.</summary>
    Unsupported,
}

namespace Ranks.EloPoints.Api;

/// <summary>The operator intent this module reads from core rather than configuring twice.</summary>
/// <remarks>
/// A module-owned record rather than core's <c>RanksPointSettings</c>: returning a contract type
/// would put it in a signature outside <see cref="RanksBridge"/>, which is the same JIT hazard the
/// <c>object?</c> field exists to prevent, one indirection further out.
///
/// Core applies none of these itself with UseCoreRankingLogic off (FR-PTS-00); they are still what
/// the operator asked for, and FR-MOD-05 rule 3 says a rating module honours them.
/// </remarks>
internal readonly record struct CoreSettings(
    bool UseCoreRankingLogic,
    int StartPoints,
    int MinPoints,
    int MinPlayers,
    bool WarmupPoints,
    bool PointsForBots);

namespace Ranks.EloPoints.Contract;

/// <summary>One player's Elo standing.</summary>
/// <remarks>
/// <paramref name="Rating"/> is the authoritative value and is fractional — core's <c>points</c>
/// holds the rounded form for the ladder and the scoreboard, so the two legitimately differ by up
/// to half a point.
/// </remarks>
public sealed record EloStanding(
    ulong SteamId,
    decimal Rating,
    int Matches,
    int Abandons,
    DateTime UpdatedAt);

/// <summary>One resolved match, newest first when returned in a list.</summary>
public sealed record EloMatchResult(
    ulong SteamId,
    decimal Delta,
    decimal RatingAfter,
    int RoundsPresent,
    bool Abandoned,
    DateTime SettledAt);

/// <summary>Read access to <c>Ranks_EloPoints</c>'s ratings.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
///
/// Unlike WeaponStats there is no unflushed-delta problem to solve: a rating is written once, at
/// match end, so the stored value is always current. A match in progress is deliberately not
/// visible here — it has no result until it ends, and exposing a provisional one would invite
/// callers to treat it as settled.
/// </remarks>
public interface IEloPointsApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.EloPoints.Api.v1";

    /// <summary>A player's standing, or null when they have never completed a rated match.</summary>
    Task<EloStanding?> GetStandingAsync(ulong steamId);

    /// <summary>The highest-rated players, descending.</summary>
    Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit = 10);

    /// <summary>True when a rated match is currently in progress and this player is in it.</summary>
    /// <remarks>
    /// Synchronous because it reads the live session set, not the database. Lets a HUD show "rating
    /// pending" rather than a stale number mid-match.
    /// </remarks>
    bool IsInRatedMatch(ulong steamId);
}

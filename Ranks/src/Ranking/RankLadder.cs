using Ranks.Config;

namespace Ranks.Ranking;

/// <summary>Resolves a point total to a ladder tier.</summary>
/// <remarks>
/// Thresholds are fixed point values; there is no percentile or position mode. The consequence is
/// that any fixed ladder saturates as a server ages — points never decay, so eventually every
/// regular sits above the top threshold and the upper bands stop distinguishing anyone.
/// </remarks>
internal sealed class RankLadder
{
    private readonly Func<IReadOnlyList<RankTier>> _tiers;

    /// <remarks>
    /// Takes a supplier rather than the config host so it re-reads on every access (the file is
    /// hot-reloaded) without naming the host, which also lets the self-check drive it directly.
    /// </remarks>
    public RankLadder(Func<IReadOnlyList<RankTier>> tiers) => _tiers = tiers;

    public IReadOnlyList<RankTier> Tiers => _tiers();

    /// <summary>The 0-based tier a point total falls in.</summary>
    /// <remarks>
    /// Walks down from the top rather than up, so the <em>highest</em> threshold met wins. The
    /// list is sorted ascending at validation, and the first entry is guaranteed to be 0, so the
    /// loop always finds one.
    /// </remarks>
    public int IndexFor(int points)
    {
        var tiers = Tiers;

        for (var i = tiers.Count - 1; i >= 0; i--)
            if (points >= tiers[i].Points)
                return i;

        return 0;
    }

    public RankTier TierFor(int points) => Tiers[IndexFor(points)];

    public RankTier? NextTier(int points)
    {
        var index = IndexFor(points);

        return index + 1 < Tiers.Count ? Tiers[index + 1] : null;
    }

    /// <summary>Points still needed for the next tier, or null at the top.</summary>
    public int? PointsToNext(int points) => NextTier(points) is { } next ? Math.Max(0, next.Points - points) : null;

    /// <summary>Progress through the current band in <c>[0, 1]</c>, for the menu progress bar.</summary>
    /// <remarks>Returns 1 at the top band — there is nothing left to progress toward.</remarks>
    public double ProgressInBand(int points)
    {
        var index = IndexFor(points);
        if (index + 1 >= Tiers.Count) return 1.0;

        var floor = Tiers[index].Points;
        var ceiling = Tiers[index + 1].Points;
        if (ceiling <= floor) return 1.0;

        return Math.Clamp((points - floor) / (double)(ceiling - floor), 0.0, 1.0);
    }
}

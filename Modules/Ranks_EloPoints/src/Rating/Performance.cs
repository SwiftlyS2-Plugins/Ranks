namespace Ranks.EloPoints.Rating;

/// <summary>Core's nine counters, diffed across one match.</summary>
/// <remarks>
/// A module-owned record rather than core's <c>RanksPlayerSnapshot</c>: returning a contract type
/// from the bridge would put it in a signature outside the one file allowed to name one, which is
/// the same JIT hazard one indirection further out.
/// </remarks>
internal readonly record struct PerfCounters(
    long Kills,
    long Deaths,
    long Assists,
    long RoundWins,
    long BombsPlanted,
    long BombsDefused)
{
    public PerfCounters Since(in PerfCounters start) => new(
        Kills - start.Kills,
        Deaths - start.Deaths,
        Assists - start.Assists,
        RoundWins - start.RoundWins,
        BombsPlanted - start.BombsPlanted,
        BombsDefused - start.BombsDefused);
}

/// <summary>Turns per-match counters into a percentile within that match.</summary>
internal static class Performance
{
    // Starting values, expected to move once there is real data. Objectives are in the measure
    // because a pure K/D bonus pays players to stop defusing the bomb.
    private const double KillWeight = 1.0;
    private const double AssistWeight = 0.5;
    private const double DeathWeight = -0.7;
    private const double RoundWinWeight = 0.3;
    private const double BombPlantWeight = 2.0;
    private const double BombDefuseWeight = 2.0;

    /// <summary>The median, used for a player whose match-start counters were never captured.</summary>
    public const double NeutralPercentile = 0.5;

    public static double Score(in PerfCounters c) =>
        KillWeight * c.Kills +
        AssistWeight * c.Assists +
        DeathWeight * c.Deaths +
        RoundWinWeight * c.RoundWins +
        BombPlantWeight * c.BombsPlanted +
        BombDefuseWeight * c.BombsDefused;

    /// <summary>Mid-rank percentile of <paramref name="score"/> within <paramref name="all"/>.</summary>
    /// <remarks>
    /// Match-relative rather than against an absolute threshold: an absolute one inflates the
    /// whole ladder over time, because the average player on a quiet server clears it. Ties share
    /// a rank, so five identical performances all land on the median rather than being ordered
    /// arbitrarily.
    /// </remarks>
    public static double Percentile(ReadOnlySpan<double> all, double score)
    {
        if (all.Length <= 1) return NeutralPercentile;

        var below = 0;
        var tied = 0;

        foreach (var other in all)
        {
            if (other < score) below++;
            else if (other == score) tied++;
        }

        // tied counts the player themselves, so one is removed before the half-credit.
        return (below + 0.5 * (tied - 1)) / (all.Length - 1);
    }
}

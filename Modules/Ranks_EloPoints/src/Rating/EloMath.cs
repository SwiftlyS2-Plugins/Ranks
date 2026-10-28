namespace Ranks.EloPoints.Rating;

/// <summary>Everything one player's match delta is computed from.</summary>
internal readonly record struct EloInputs(
    double K,
    int ConfidenceRounds,
    int RoundsPresent,
    int MatchRounds,
    double SumActual,
    double SumExpected,
    double PerformanceBonus,
    double Percentile);

/// <summary>The rating formula. Pure, and names no engine or contract type.</summary>
internal static class EloMath
{
    /// <summary>Smallest delta a decided result may produce, so a win can never read as a loss.</summary>
    /// <remarks>A correctness guard rather than a tuning knob, so deliberately not configurable.</remarks>
    internal const double Epsilon = 0.01;

    /// <summary>Standard Elo expectation: a 400-point lead is about 0.909.</summary>
    public static double Expected(double own, double opponent) =>
        1.0 / (1.0 + Math.Pow(10.0, (opponent - own) / 400.0));

    /// <summary>Pulls an actual score toward its expectation in proportion to how little evidence there is.</summary>
    public static double Shrink(double meanActual, double meanExpected, int rounds, int confidence) =>
        rounds + confidence <= 0
            ? meanExpected
            : (rounds * meanActual + confidence * meanExpected) / (rounds + confidence);

    /// <summary>The match result, through the envelope and the sign floor. Excludes the abandon penalty.</summary>
    public static double MatchDelta(in EloInputs i)
    {
        if (i.RoundsPresent <= 0 || i.MatchRounds <= 0) return 0.0;

        var meanActual = i.SumActual / i.RoundsPresent;
        var meanExpected = i.SumExpected / i.RoundsPresent;
        var adjusted = Shrink(meanActual, meanExpected, i.RoundsPresent, i.ConfidenceRounds);

        // Capped at 1: a mid-match estimate of the match length can be shorter than the rounds a
        // player turns out to have been present for.
        var participation = Math.Min(1.0, (double)i.RoundsPresent / i.MatchRounds);
        var outcome = i.K * (adjusted - meanExpected) * participation;

        // Clamped before the bonus is added: the envelope bounds what one result may move, and the
        // bonus is not part of the result.
        outcome = Math.Clamp(outcome, -i.K * meanExpected, i.K * (1.0 - meanExpected));

        return SignFloor(outcome, outcome + i.PerformanceBonus * (i.Percentile - 0.5));
    }

    /// <summary>Applies the penalty and the floor. Separate so the penalty escapes the sign floor.</summary>
    /// <remarks>
    /// The penalty is allowed to turn a win into a net loss — that is the whole point of it — so
    /// it is deliberately outside <see cref="SignFloor"/>.
    /// </remarks>
    public static double Apply(double rating, double matchDelta, double penalty, double minRating) =>
        Math.Max(minRating, rating + matchDelta - penalty);

    /// <summary>Stops the performance bonus flipping the sign of the result it is adjusting.</summary>
    /// <remarks>
    /// Keyed on the outcome's sign rather than the match result, which is what makes it correct in
    /// RoundAccumulation: a player who joined late and lost every round they were present for
    /// should still go down even if their team went on to win. The case the envelope misses is a
    /// heavy favourite (E = 0.95) winning +2.5 and a bottom-percentile bonus subtracting 5.
    /// </remarks>
    private static double SignFloor(double outcome, double delta) => outcome switch
    {
        > 0 when delta < Epsilon => Epsilon,
        < 0 when delta > -Epsilon => -Epsilon,
        _ => delta,
    };
}

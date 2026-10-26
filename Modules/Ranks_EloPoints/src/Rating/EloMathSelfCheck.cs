namespace Ranks.EloPoints.Rating;

/// <summary>Asserts the rating formula at load. Throws rather than logging.</summary>
/// <remarks>
/// Every failure mode here is silent in production: a sign error in the exponent is invisible on
/// even teams, a missing clamp only shows on a lopsided match, and a wrong shrinkage weight just
/// makes the ladder move at the wrong speed. None of it produces an exception or a wrong-looking
/// number, so an assertion at load is the only place it can be caught.
/// </remarks>
internal static class EloMathSelfCheck
{
    private const double Tolerance = 0.01;

    public static void Run()
    {
        ExpectationIsSymmetric();
        NoEvidenceMovesNothing();
        ShrinkageApproachesTheRawResultAsEvidenceGrows();
        PublishedFiguresStillHold();
        OutcomeStaysInsideTheEnvelope();
        TheBonusCannotTurnAWinIntoALoss();
        ThePenaltyIsAlwaysAFlatDeduction();
        PercentileHandlesTiesAndSingletons();
    }

    /// <remarks>An inverted sign in the exponent is invisible between equally rated teams.</remarks>
    private static void ExpectationIsSymmetric()
    {
        Require(Close(EloMath.Expected(1000, 1000), 0.5), "an even matchup did not expect 0.5");
        Require(Close(EloMath.Expected(1400, 1000), 0.9091), "a 400-point lead did not expect 0.9091");
        Require(Close(EloMath.Expected(1400, 1000) + EloMath.Expected(1000, 1400), 1.0), "a matchup's two expectations did not sum to 1");
        Require(EloMath.Expected(1400, 1000) > EloMath.Expected(1000, 1400), "the favourite was not expected to win more often");
    }

    /// <remarks>A session with no rounds must divide cleanly, not produce NaN.</remarks>
    private static void NoEvidenceMovesNothing()
    {
        var delta = EloMath.MatchDelta(new EloInputs(50, 10, RoundsPresent: 0, MatchRounds: 24, 0, 0, 10, 1.0));

        Require(delta == 0.0, $"an empty session moved the rating by {delta}");
        Require(Close(EloMath.Shrink(1.0, 0.5, rounds: 0, confidence: 10), 0.5), "shrinkage with no rounds did not collapse to the expectation");
    }

    /// <remarks>An inverted weighting would damp long sessions instead of short ones.</remarks>
    private static void ShrinkageApproachesTheRawResultAsEvidenceGrows()
    {
        var previous = double.MinValue;

        foreach (var rounds in (ReadOnlySpan<int>)[1, 2, 5, 10, 25, 100])
        {
            var adjusted = EloMath.Shrink(meanActual: 1.0, meanExpected: 0.5, rounds, confidence: 10);

            Require(adjusted > previous, $"shrinkage at {rounds} rounds ({adjusted:N4}) did not exceed the previous sample ({previous:N4})");
            Require(adjusted <= 1.0, $"shrinkage at {rounds} rounds overshot the raw result at {adjusted:N4}");

            previous = adjusted;
        }

        Require(Close(EloMath.Shrink(1.0, 0.5, rounds: 10, confidence: 0), 1.0), "shrinkage with confidence 0 did not leave the result untouched");
    }

    /// <remarks>These four figures are published in docs/features.md; drifting from them silently makes the docs wrong.</remarks>
    private static void PublishedFiguresStillHold()
    {
        foreach (var (label, inputs, expected) in (ReadOnlySpan<(string, EloInputs, double)>)
                 [
                     ("13-0 even teams", Even(rounds: 13, matchRounds: 13, won: 13), 14.13),
                     ("13-8 full match", Even(rounds: 21, matchRounds: 21, won: 13), 4.03),
                     ("joined at round 18, won all 4", Even(rounds: 4, matchRounds: 21, won: 4), 1.36),
                     ("two-round cameo", Even(rounds: 2, matchRounds: 21, won: 2), 0.40),
                 ])
        {
            var delta = EloMath.MatchDelta(inputs);

            Require(Close(delta, expected), $"{label} produced {delta:N2}, expected {expected:N2}");
        }
    }

    /// <remarks>
    /// Checked with the bonus disabled, because the bonus is deliberately applied after the clamp
    /// and may legitimately carry the delta outside the envelope by up to half its weight.
    /// </remarks>
    private static void OutcomeStaysInsideTheEnvelope()
    {
        var random = new Random(20261026);

        for (var i = 0; i < 20_000; i++)
        {
            var k = random.Next(1, 201);
            var rounds = random.Next(1, 40);
            var matchRounds = random.Next(1, 40);
            var expected = random.NextDouble();
            var won = random.Next(0, rounds + 1);

            var inputs = new EloInputs(k, random.Next(0, 101), rounds, matchRounds, won, expected * rounds, PerformanceBonus: 0, Percentile: random.NextDouble());
            var delta = EloMath.MatchDelta(inputs);

            Require(delta >= -k * expected - Tolerance && delta <= k * (1.0 - expected) + Tolerance,
                $"delta {delta:N4} escaped the envelope [{-k * expected:N4}, {k * (1.0 - expected):N4}] at K={k}, E={expected:N4}, n={rounds}");
        }

        CorruptedAccrualIsStillBounded();
    }

    /// <remarks>
    /// The clamp is unreachable on valid input: with an actual score in [0, 1], K * (S_adj - E)
    /// already lands inside the envelope algebraically. It exists to bound an upstream accrual
    /// bug — a round counted twice would otherwise move a rating arbitrarily far — so it is tested
    /// with the input such a bug produces rather than with the randomised sweep above, which can
    /// never reach it.
    /// </remarks>
    private static void CorruptedAccrualIsStillBounded()
    {
        const double K = 50;
        const double Expected = 0.5;
        const int Rounds = 20;

        // Every round credited twice: a mean actual score of 2.0, which is not a score.
        var delta = EloMath.MatchDelta(new EloInputs(K, 10, Rounds, Rounds, SumActual: Rounds * 2, SumExpected: Expected * Rounds, PerformanceBonus: 0, Percentile: 0.5));

        Require(delta <= K * (1.0 - Expected) + Tolerance,
            $"a double-counted accrual produced {delta:N4}, which escapes the envelope ceiling of {K * (1.0 - Expected):N4}");
    }

    /// <remarks>
    /// The case the envelope does not catch: a heavy favourite wins +2.5 and a bottom-percentile
    /// bonus subtracts 5, landing at -2.5 comfortably inside [-47.5, +2.5].
    /// </remarks>
    private static void TheBonusCannotTurnAWinIntoALoss()
    {
        var expected = 0.95;
        var rounds = 20;

        var won = EloMath.MatchDelta(new EloInputs(50, 0, rounds, rounds, rounds, expected * rounds, PerformanceBonus: 10, Percentile: 0.0));
        Require(won > 0, $"winning every round produced {won:N4}, which is not a gain");

        var lost = EloMath.MatchDelta(new EloInputs(50, 0, rounds, rounds, 0, 0.05 * rounds, PerformanceBonus: 10, Percentile: 1.0));
        Require(lost < 0, $"losing every round produced {lost:N4}, which is not a loss");
    }

    /// <remarks>Folding the penalty inside the sign floor would cap it at Epsilon on a win.</remarks>
    private static void ThePenaltyIsAlwaysAFlatDeduction()
    {
        foreach (var delta in (ReadOnlySpan<double>)[-40.0, -1.0, 0.0, 1.0, 40.0])
        {
            var without = EloMath.Apply(1000, delta, penalty: 0, minRating: 0);
            var with = EloMath.Apply(1000, delta, penalty: 25, minRating: 0);

            Require(Close(without - with, 25.0), $"a penalty of 25 moved a delta of {delta:N2} by {without - with:N4}");
        }

        Require(Close(EloMath.Apply(100, -500, penalty: 25, minRating: 100), 100), "the rating floor did not hold");
    }

    /// <remarks>Ties must share a rank, or identical performances are ordered arbitrarily.</remarks>
    private static void PercentileHandlesTiesAndSingletons()
    {
        Require(Close(Performance.Percentile([5.0], 5.0), 0.5), "a single player was not placed at the median");

        ReadOnlySpan<double> tied = [3.0, 3.0, 3.0, 3.0];
        Require(Close(Performance.Percentile(tied, 3.0), 0.5), "four identical scores were not all placed at the median");

        ReadOnlySpan<double> spread = [1.0, 2.0, 3.0, 4.0, 5.0];
        Require(Close(Performance.Percentile(spread, 1.0), 0.0), "the worst score was not placed at 0");
        Require(Close(Performance.Percentile(spread, 5.0), 1.0), "the best score was not placed at 1");
        Require(Close(Performance.Percentile(spread, 3.0), 0.5), "the middle score was not placed at the median");
    }

    private static EloInputs Even(int rounds, int matchRounds, int won) =>
        new(K: 50, ConfidenceRounds: 10, rounds, matchRounds, SumActual: won, SumExpected: 0.5 * rounds, PerformanceBonus: 0, Percentile: 0.5);

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= Tolerance;

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks_EloPoints: rating self-check failed — {failure}.");
    }
}

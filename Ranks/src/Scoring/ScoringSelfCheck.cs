using Ranks.Config;
using Ranks.Ranking;

namespace Ranks.Scoring;

/// <summary>Asserts the scoring and ladder invariants the config's documentation promises.</summary>
/// <remarks>
/// These exist because the failure mode is silence: a broken anchor makes every configured
/// <c>core.kill</c> value mean something other than what it says, and nothing in the game reports
/// it — a server just scores slightly wrong forever. Microseconds at load, and the only check this
/// logic gets.
/// </remarks>
internal static class ScoringSelfCheck
{
    public static void Run()
    {
        EqualPlayersScoreTheConfiguredValue();
        DifferenceIsBoundedAndSymmetric();
        AnchorTableHolds();
        LadderPicksTheHighestThresholdMet();
    }

    /// <remarks>The anchor the config documents — any replacement formula must preserve it.</remarks>
    private static void EqualPlayersScoreTheConfiguredValue()
    {
        foreach (var points in (ReadOnlySpan<int>)[0, 1, 500, 30_000])
        {
            var d = DynamicScaling.Difference(points, points);
            Require(d == 0, $"equal points produced d = {d}");

            var m = DynamicScaling.Multiplier(d, 0.5, 3.0);
            Require(m == 1.0, $"equal points produced a multiplier of {m}, not 1.0");
        }
    }

    private static void DifferenceIsBoundedAndSymmetric()
    {
        foreach (var (victim, attacker) in (ReadOnlySpan<(int, int)>)[(0, 0), (10_000, 1), (1, 10_000), (900, 1000), (1000, 900)])
        {
            var d = DynamicScaling.Difference(victim, attacker);

            Require(d is >= -1.0 and <= 1.0, $"d was {d} for victim {victim} / attacker {attacker}");
            Require(Math.Abs(d + DynamicScaling.Difference(attacker, victim)) < 1e-9, $"d is not symmetric at victim {victim} / attacker {attacker}");
        }
    }

    /// <remarks>The five rows FR-PTS-05 tabulates, at the shipped 0.5 / 3.0 bounds.</remarks>
    private static void AnchorTableHolds()
    {
        const double Min = 0.5;
        const double Max = 3.0;

        Expect(1000, 900, 1.20, Min, Max);
        Expect(1000, 100, 2.80, Min, Max);
        Expect(100, 1000, 0.55, Min, Max);
        Expect(0, 1000, Min, Min, Max);

        // A victim on zero against an attacker on zero is two fresh players, not a floor case.
        Expect(0, 0, 1.0, Min, Max);
    }

    private static void Expect(int victim, int attacker, double expected, double min, double max)
    {
        var actual = DynamicScaling.Multiplier(DynamicScaling.Difference(victim, attacker), min, max);

        Require(Math.Abs(actual - expected) < 1e-9,
            $"victim {victim} / attacker {attacker} gave {actual:0.####}, expected {expected:0.####}");
    }

    /// <remarks>
    /// Walks the shipped ladder rather than a synthetic one, so a bad edit to the seed is caught
    /// here instead of on a live scoreboard.
    /// </remarks>
    private static void LadderPicksTheHighestThresholdMet()
    {
        var tiers = LadderSeed.Premier();
        var ladder = new RankLadder(() => tiers);

        Require(tiers[0].Points == 0, "the shipped ladder does not start at 0 points");

        for (var i = 0; i < tiers.Count; i++)
        {
            Require(ladder.IndexFor(tiers[i].Points) == i, $"exactly meeting tier {i}'s threshold did not resolve to it");

            if (i > 0)
                Require(ladder.IndexFor(tiers[i].Points - 1) == i - 1, $"one point below tier {i} did not resolve to tier {i - 1}");
        }

        Require(ladder.IndexFor(int.MaxValue) == tiers.Count - 1, "a huge total did not resolve to the top tier");
        Require(ladder.PointsToNext(tiers[^1].Points) is null, "the top tier reported a next tier");
        Require(ladder.ProgressInBand(tiers[^1].Points) == 1.0, "the top tier did not report full progress");
    }

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks: scoring self-check failed — {failure}.");
    }
}

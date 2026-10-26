using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ranks.Prestige.Config;

namespace Ranks.Prestige.Prestiging;

/// <summary>Asserts the ladder mapping the README promises.</summary>
/// <remarks>
/// The failure mode is silence: a wrong deduction shows a plausible number on the scoreboard and
/// nothing reports it, so the first sign is a player asking why their rank fell. Microseconds at
/// load, and the only check this logic gets.
/// </remarks>
internal static class PrestigeSelfCheck
{
    public static void Run()
    {
        ThresholdsResolveToTheHighestLevelReached();
        CrossingAThresholdResetsTheDisplayToZero();
        OrderAndDuplicatesDoNotChangeTheOutcome();
    }

    private static PrestigeLadder Ladder(params (int Level, int Min)[] levels)
    {
        var config = new PrestigeConfig
        {
            Levels = [.. levels.Select(l => new PrestigeLevel { Level = l.Level, MinimumPoints = l.Min })],
        };

        return PrestigeLadder.Build(config, NullLogger.Instance);
    }

    private static void ThresholdsResolveToTheHighestLevelReached()
    {
        var ladder = Ladder((1, 100), (2, 200), (3, 300));

        Require(ladder.Resolve(0).Level == 0, "below the first threshold must be level 0");
        Require(ladder.Resolve(99).Level == 0, "one point short must not promote");
        Require(ladder.Resolve(100).Level == 1, "exactly the threshold must promote");
        Require(ladder.Resolve(250).Level == 2, "between thresholds must take the lower level");
        Require(ladder.Resolve(10_000).Level == 3, "far above the top must stay at the top level");
    }

    /// <remarks>This is the property the whole feature exists for: the visible ladder restarts.</remarks>
    private static void CrossingAThresholdResetsTheDisplayToZero()
    {
        var ladder = Ladder((1, 100), (2, 200));

        Require(ladder.Resolve(100).DisplayPoints == 0, "crossing must reset the display to 0");
        Require(ladder.Resolve(150).DisplayPoints == 50, "display must be points minus the threshold");
        Require(ladder.Resolve(200).DisplayPoints == 0, "the next level must reset it again");
        Require(ladder.Resolve(50).DisplayPoints == 50, "an unpromoted player must display their own points");

        foreach (var points in new[] { 0, 1, 99, 100, 101, 199, 200, 201, 5_000 })
            Require(ladder.Resolve(points).DisplayPoints >= 0, $"display must never go negative (points={points})");

        Require(ladder.PointsToNext(40) == 60, "progress must count stored points, not displayed ones");
        Require(ladder.PointsToNext(5_000) is null, "the top level must report no next step");
    }

    private static void OrderAndDuplicatesDoNotChangeTheOutcome()
    {
        var shuffled = Ladder((3, 300), (1, 100), (2, 200));

        Require(shuffled.Resolve(250).Level == 2, "config order must not change resolution");
        Require(shuffled.Count == 3, "every valid level must survive the build");

        // Two levels on one threshold make the lower unreachable, so the higher is kept.
        var duplicated = Ladder((1, 100), (2, 100));

        Require(duplicated.Count == 1, "a duplicate threshold must be dropped");
        Require(duplicated.Resolve(100).Level == 2, "the higher level must win a duplicate threshold");

        var invalid = Ladder((0, 100), (1, 0), (2, 200));

        Require(invalid.Count == 1, "levels at or below zero must be dropped");
        Require(invalid.Resolve(200).Level == 2, "the surviving level must still resolve");
    }

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks_Prestige: self-check failed — {failure}.");
    }
}

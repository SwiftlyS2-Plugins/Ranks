using Microsoft.Extensions.Logging;
using Ranks.Prestige.Config;

namespace Ranks.Prestige.Prestiging;

/// <summary>What a player's stored points resolve to: a level, and what the scoreboard shows.</summary>
/// <param name="Level">0 when the player has not reached the first step.</param>
/// <param name="DisplayPoints">Stored points minus the level's threshold; never negative.</param>
/// <param name="RankMode">Empty when this level does not change the scoreboard mode.</param>
public readonly record struct PrestigeStanding(int Level, int DisplayPoints, string RankMode);

/// <summary>The ladder, ordered and validated once at load.</summary>
/// <remarks>
/// Pure and position-independent: the operator may list levels in any order, and nothing here
/// touches core, so <see cref="PrestigeSelfCheck"/> can assert the whole mapping without a server.
/// </remarks>
internal sealed class PrestigeLadder
{
    private readonly PrestigeLevel[] _levels;

    private PrestigeLadder(PrestigeLevel[] levels) => _levels = levels;

    public int Count => _levels.Length;

    public IReadOnlyList<PrestigeLevel> Levels => _levels;

    /// <summary>Drops unusable entries with a named reason rather than failing the load.</summary>
    /// <remarks>
    /// A duplicate threshold would make two levels resolve the same points, so the later one is
    /// unreachable; keeping the higher level matches "the highest level the points reach wins".
    /// </remarks>
    public static PrestigeLadder Build(PrestigeConfig config, ILogger logger)
    {
        var usable = new List<PrestigeLevel>();

        foreach (var level in config.Levels)
        {
            if (level.Level <= 0)
            {
                logger.LogWarning("Ranks_Prestige: skipping a level with Level {Level}; must be above zero.", level.Level);
                continue;
            }

            if (level.MinimumPoints <= 0)
            {
                logger.LogWarning(
                    "Ranks_Prestige: skipping level {Level}; MinimumPoints must be above zero.", level.Level);
                continue;
            }

            usable.Add(level);
        }

        var ordered = usable
            .GroupBy(l => l.MinimumPoints)
            .Select(g => g.OrderByDescending(l => l.Level).First())
            .OrderBy(l => l.MinimumPoints)
            .ToArray();

        if (ordered.Length != usable.Count)
        {
            logger.LogWarning(
                "Ranks_Prestige: {Dropped} level(s) shared a MinimumPoints with another and were dropped.",
                usable.Count - ordered.Length);
        }

        return new PrestigeLadder(ordered);
    }

    /// <summary>The highest level the points reach, and the display they leave behind.</summary>
    public PrestigeStanding Resolve(int points)
    {
        PrestigeLevel? reached = null;

        // Ascending, so the last match is the highest threshold met.
        foreach (var level in _levels)
        {
            if (points < level.MinimumPoints) break;

            reached = level;
        }

        if (reached is null) return new PrestigeStanding(0, points, string.Empty);

        return new PrestigeStanding(
            reached.Level,
            Math.Max(0, points - reached.MinimumPoints),
            reached.RankMode ?? string.Empty);
    }

    /// <summary>Stored points still needed for the next level, or null at the top.</summary>
    public int? PointsToNext(int points)
    {
        foreach (var level in _levels)
            if (points < level.MinimumPoints) return level.MinimumPoints - points;

        return null;
    }
}

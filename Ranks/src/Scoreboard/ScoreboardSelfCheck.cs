using Ranks.Config;
using Ranks.Contract;

namespace Ranks.Scoreboard;

/// <summary>Asserts the scoreboard's engine mapping and the Custom-mode icon formula.</summary>
/// <remarks>
/// These exist because the failure mode is silence: CS2 renders nothing at all for an
/// unrecognised <c>m_iCompetitiveRankType</c> and reports no error, so a wrong value looks
/// identical to the feature being switched off. The plugin-level enum deliberately does not
/// hold engine values, which is exactly how it shipped writing <c>(byte)mode</c> for a release.
/// </remarks>
internal static class ScoreboardSelfCheck
{
    public static void Run()
    {
        EveryVisibleModeMapsToAKnownEngineType();
        CustomIconMatchesTheReferenceMapping();
        OverlayModeFallsBackRatherThanBlanking();
    }

    /// <remarks>
    /// `0` is the engine's "no rank", so a visible mode resolving to it is the silent-failure case.
    /// </remarks>
    private static void EveryVisibleModeMapsToAKnownEngineType()
    {
        (RankDisplayMode Mode, byte Expected)[] expected =
        [
            (RankDisplayMode.Premier, 11),
            (RankDisplayMode.Competitive, 12),
            (RankDisplayMode.Wingman, 7),
            (RankDisplayMode.DangerZone, 10),
            (RankDisplayMode.Custom, 12),
        ];

        foreach (var (mode, want) in expected)
        {
            var got = ScoreboardService.EngineRankType(mode);

            Require(got == want, $"{mode} maps to engine rank type {got}, expected {want}");
            Require(got != 0, $"{mode} maps to 0, which the engine renders as no rank at all");
        }

        Require(ScoreboardService.EngineRankType(RankDisplayMode.Off) == 0, "Off must map to 0");
    }

    /// <remarks>
    /// The shipped defaults (base 0, max 18, margin 0) must place ladder position 1 on icon 0 and
    /// saturate past position 18, which is what an existing K4-LevelRanks icon addon is indexed for.
    /// </remarks>
    private static void CustomIconMatchesTheReferenceMapping()
    {
        var defaults = new ScoreboardOptions();

        Require(ScoreboardService.CustomIcon(defaults, 1) == 0, "position 1 must land on icon 0 at the defaults");
        Require(ScoreboardService.CustomIcon(defaults, 18) == 17, "position 18 must land on icon 17 at the defaults");
        Require(ScoreboardService.CustomIcon(defaults, 19) == 18, "a position past CustomRankMax must saturate");
        Require(ScoreboardService.CustomIcon(defaults, 99) == 18, "saturation must not keep climbing");

        var shifted = new ScoreboardOptions { CustomRankBase = 5, CustomRankMax = 10, CustomRankMargin = 2 };

        Require(ScoreboardService.CustomIcon(shifted, 3) == 5, "base must place the first counted position");
        Require(ScoreboardService.CustomIcon(shifted, 11) == 13, "past max must be base + max - margin");

        // A margin larger than the position drives the index negative, and the engine treats the
        // value as an array lookup. Only a zero base can reach it, which is the shipped default.
        var underflow = new ScoreboardOptions { CustomRankBase = 0, CustomRankMax = 18, CustomRankMargin = 2 };

        Require(ScoreboardService.CustomIcon(underflow, 1) == 0, "an underflowing position must clamp to 0");
    }

    /// <remarks>
    /// The overlay's mode crosses an assembly boundary as a string, so a module typo is reachable.
    /// Falling through to Off would blank every scoreboard on the server for a one-character slip.
    /// </remarks>
    private static void OverlayModeFallsBackRatherThanBlanking()
    {
        var config = new RanksConfig();
        config.Scoreboard.RankMode = nameof(RankDisplayMode.Premier);

        Require(ScoreboardService.ResolveMode(config, null) == RankDisplayMode.Premier,
            "no overlay must use the configured mode");

        config.Scoreboard.RankMode = "Premiere";
        Require(ScoreboardService.ResolveMode(config, null) == RankDisplayMode.Premier,
            "an unparseable configured mode must fall back to Premier, not Off");

        config.Scoreboard.RankMode = "3";
        Require(ScoreboardService.ResolveMode(config, null) == RankDisplayMode.Wingman,
            "the numeric form a pre-string config carries must still parse");

        config.Scoreboard.RankMode = nameof(RankDisplayMode.Premier);

        Require(ScoreboardService.ResolveMode(config, new RanksDisplayOverride()) == RankDisplayMode.Premier,
            "an overlay naming no mode must use the configured mode");

        Require(ScoreboardService.ResolveMode(config, new RanksDisplayOverride(RankMode: "Wingman")) == RankDisplayMode.Wingman,
            "an overlay must be able to change the mode");

        Require(ScoreboardService.ResolveMode(config, new RanksDisplayOverride(RankMode: "dangerzone")) == RankDisplayMode.DangerZone,
            "mode names must parse case-insensitively");

        Require(ScoreboardService.ResolveMode(config, new RanksDisplayOverride(RankMode: "Premiere")) == RankDisplayMode.Premier,
            "an unparseable mode must fall back, not blank the scoreboard");

        foreach (var name in new[]
        {
            RanksDisplayModes.Off, RanksDisplayModes.Premier, RanksDisplayModes.Competitive,
            RanksDisplayModes.Wingman, RanksDisplayModes.DangerZone, RanksDisplayModes.Custom,
        })
        {
            Require(Enum.TryParse<RankDisplayMode>(name, out _),
                $"contract constant '{name}' has no matching RankDisplayMode member");
        }
    }

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks: scoreboard self-check failed — {failure}.");
    }
}


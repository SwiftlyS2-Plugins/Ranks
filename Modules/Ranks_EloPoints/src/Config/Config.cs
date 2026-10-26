using System.ComponentModel;

namespace Ranks.EloPoints.Config;

/// <summary>How a match result is turned into actual and expected scores.</summary>
internal enum RatingMode
{
    /// <summary>Scores every round against the expectation at that moment, then averages.</summary>
    RoundAccumulation,

    /// <summary>Textbook Elo: one result against one expectation, scaled by participation.</summary>
    Classic,
}

internal sealed class RatingConfig
{
    /// <remarks>
    /// A string rather than the enum: an unrecognised value would otherwise bind to whichever
    /// member is zero, and the operator's typo would look like working software. Parsed in Clamp,
    /// which reports the fallback.
    /// </remarks>
    [Description("RoundAccumulation scores every round and averages; Classic scores the match result once.")]
    public string Mode { get; set; } = nameof(RatingMode.RoundAccumulation);

    [Description("Maximum swing between two evenly matched teams. 50 gives +/-25 on a clean win or loss.")]
    public int K { get; set; } = 50;

    [Description("Maximum swing from individual performance within the match. 0 rates on the result alone.")]
    public int PerformanceBonus { get; set; } = 10;

    [Description("Rounds of evidence a result is worth before it is believed in full. Higher damps short sessions harder. 0 disables shrinkage.")]
    public int ConfidenceRounds { get; set; } = 10;

    [Description("Fallback match length, used only while a match is running and mp_maxrounds cannot be read.")]
    public int ExpectedRounds { get; set; } = 24;
}

internal sealed class AbandonConfig
{
    /// <remarks>
    /// Keep it at or above K/2. Leaving early accrues almost no loss, so the penalty has to cover
    /// what staying would still have cost; the threshold that always deters is K * E, and K/2
    /// covers balanced matches exactly. docs/verify-figures.ps1 reproduces the table.
    /// </remarks>
    [Description("Deducted on top of the match result when a player leaves and never returns. 0 disables. Keep at or above half of K.")]
    public int Penalty { get; set; } = 25;
}

internal sealed class GeneralConfig
{
    [Description("Narrate expected flow. Warnings and errors are never gated by this.")]
    public bool DetailedLogging { get; set; }
}

/// <summary>The module's <c>config.jsonc</c>.</summary>
internal sealed class EloConfig
{
    public RatingConfig Rating { get; set; } = new();

    public AbandonConfig Abandon { get; set; } = new();

    public GeneralConfig General { get; set; } = new();

    /// <summary>The parsed <see cref="RatingConfig.Mode"/>, resolved by <see cref="Clamp"/>.</summary>
    public RatingMode ResolvedMode { get; private set; } = RatingMode.RoundAccumulation;

    /// <summary>True when Mode did not parse, so the caller can warn once rather than per use.</summary>
    public bool ModeUnrecognised { get; private set; }

    /// <summary>Clamps out-of-range values rather than throwing — these are tuning errors.</summary>
    public void Clamp()
    {
        ModeUnrecognised = !Enum.TryParse<RatingMode>(Rating.Mode, ignoreCase: true, out var mode);
        ResolvedMode = ModeUnrecognised ? RatingMode.RoundAccumulation : mode;

        Rating.K = Math.Clamp(Rating.K, 1, 200);
        Rating.PerformanceBonus = Math.Clamp(Rating.PerformanceBonus, 0, 100);
        Rating.ConfidenceRounds = Math.Clamp(Rating.ConfidenceRounds, 0, 100);
        Rating.ExpectedRounds = Math.Clamp(Rating.ExpectedRounds, 1, 200);
        Abandon.Penalty = Math.Max(0, Abandon.Penalty);
    }
}

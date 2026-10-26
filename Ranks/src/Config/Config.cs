namespace Ranks.Config;

/// <summary>Everything in <c>config.jsonc</c>.</summary>
/// <remarks>
/// The framework creates the file from this model on first run and only ever reads it
/// afterwards, so these defaults are literally the shipped config — a property without a
/// sensible one ships as a value the operator has to discover and fix.
/// </remarks>
public sealed class RanksConfig
{
    public DatabaseOptions Database { get; set; } = new();
    public RankOptions Rank { get; set; } = new();
    public ScoreboardOptions Scoreboard { get; set; } = new();
    public PointsOptions Points { get; set; } = new();
    public VipOptions Vip { get; set; } = new();
    public GeneralOptions General { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <remarks>
    /// Names an entry in SwiftlyS2's global <c>configs/database.jsonc</c>; "default" is the
    /// entry a stock install ships. An unknown name silently resolves to the default
    /// connection, so core opens it at boot and throws rather than inheriting that (FR-DB-02).
    /// </remarks>
    public string Connection { get; set; } = "default";

    /// <remarks>Normalised to one trailing underscore, so "awp", "awp_" and "_awp" agree (FR-DB-03).</remarks>
    public string TablePrefix { get; set; } = "";

    /// <remarks>Normalised to one leading underscore (FR-DB-03).</remarks>
    public string TableSuffix { get; set; } = "";

    public int SaveIntervalSeconds { get; set; } = 300;

    /// <remarks><c>0</c> never purges.</remarks>
    public int PurgeDays { get; set; } = 0;

    /// <remarks>
    /// <c>0</c> runs roll-up and purge only on a cold load. Above zero, they also run on an
    /// empty server once the interval has elapsed (FR-DB-09).
    /// </remarks>
    public int MaintenanceIntervalHours { get; set; } = 0;
}

public sealed class RankOptions
{
    /// <remarks>
    /// <c>false</c> turns core into a host: its handlers award nothing and its gates and
    /// multipliers go inert, while stat counters, the ladder and the API stay live (FR-PTS-00).
    /// </remarks>
    public bool UseCoreRankingLogic { get; set; } = true;

    public int MinPlayers { get; set; } = 4;

    /// <remarks>FR-PTS-05's multiplier formula assumes non-negative points, so this cannot go below zero.</remarks>
    public int MinPoints { get; set; } = 0;

    public int StartPoints { get; set; } = 0;

    /// <remarks><c>false</c> awards no points during warmup.</remarks>
    public bool WarmupPoints { get; set; } = false;

    /// <remarks>
    /// Unlike the context gates, this suppresses the <em>stat counters</em> as well as the
    /// award: a bot is not a player, so a kill on one is not a fact about play (FR-PTS-03).
    /// </remarks>
    public bool PointsForBots { get; set; } = false;

    public bool FfaMode { get; set; } = false;
}

/// <summary>How the scoreboard renders a player's rank.</summary>
/// <remarks>
/// Plugin-level values, not engine ones. <c>m_iCompetitiveRankType</c> takes a different
/// set and these must be translated to it — a wrong engine value renders nothing at all,
/// with no error (FR-SCB-03).
/// </remarks>
public enum RankDisplayMode
{
    Off = 0,
    Premier = 1,
    Competitive = 2,
    Wingman = 3,
    DangerZone = 4,
}

public sealed class ScoreboardOptions
{
    /// <remarks>
    /// Premier renders the point total itself as a CS Rating, coloured by CS2's own bands,
    /// which is what makes the seven-band ladder legible without mapping anything.
    /// </remarks>
    public RankDisplayMode RankMode { get; set; } = RankDisplayMode.Premier;

    public bool ScoreSync { get; set; } = false;

    public bool Clantags { get; set; } = true;

    /// <remarks>Only Competitive mode reads this; the other modes render natively and ignore it.</remarks>
    public int SkillGroupOffset { get; set; } = 0;
}

public sealed class PointsOptions
{
    public bool DynamicKillPoints { get; set; } = true;

    public double DynamicKillMin { get; set; } = 0.5;

    public double DynamicKillMax { get; set; } = 3.0;

    public bool DynamicDeathPoints { get; set; } = true;

    public double DynamicDeathMin { get; set; } = 0.5;

    public double DynamicDeathMax { get; set; } = 3.0;

    /// <remarks>
    /// Source units, at roughly 2.54 cm each — so 2000 is about 50 m. The unit is ambiguous
    /// enough that the shipped comment must state the ratio, or the next reader converts at
    /// 1.905 cm and lands on 38 m.
    /// </remarks>
    public int LongDistanceUnits { get; set; } = 2000;

    /// <remarks><c>0</c> puts no time limit on a streak; the round already bounds it.</remarks>
    public int SecondsBetweenKills { get; set; } = 0;

    /// <remarks>Streaks are per-round accumulation, so carrying one across a round boundary is the exception.</remarks>
    public bool KillstreakResetOnRoundEnd { get; set; } = true;

    public bool ShowPlayerNames { get; set; } = false;
}

public sealed class VipOptions
{
    public double Multiplier { get; set; } = 1.25;

    /// <remarks>
    /// Namespaced to this plugin so it cannot collide with another's grants, and awards
    /// nothing until an operator grants it — discoverable without being on by accident.
    /// </remarks>
    public List<string> Flags { get; set; } = ["@ranks/vip"];
}

public sealed class GeneralOptions
{
    /// <remarks>
    /// Gates successful-flow narration only. Warnings, errors and thrown exceptions are
    /// never suppressed by it.
    /// </remarks>
    public bool DetailedLogging { get; set; } = false;

    public bool KillStreaksEnabled { get; set; } = true;

    public bool RoundEndSummary { get; set; } = false;

    public bool ShowPointMessages { get; set; } = true;

    public bool ShowRankChangeMessages { get; set; } = true;
}

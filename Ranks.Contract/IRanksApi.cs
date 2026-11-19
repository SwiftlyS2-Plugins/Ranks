using System.Reflection;
using SwiftlyS2.Shared.Players;

namespace Ranks.Contract;

/// <summary>The <c>Ranks.Api.v1</c> shared interface.</summary>
/// <remarks>
/// Additive only after 1.0 — there is no package registry and therefore no version negotiation,
/// so a renamed or re-signed member is a <c>MissingMethodException</c> at call time rather than a
/// build error anyone sees (FR-API-00).
/// </remarks>
public interface IRanksApi
{
    /// <summary>The shared-interface key modules bind against.</summary>
    const string InterfaceKey = "Ranks.Api.v1";

    // ---- FR-API-10 readiness -------------------------------------------------

    /// <summary>True once configuration is bound and the schema probe has run.</summary>
    bool IsCoreReady { get; }

    /// <remarks>
    /// Fires once per load cycle. Check <see cref="IsCoreReady"/> first — a module loading after
    /// core would otherwise subscribe to an event that has already fired.
    /// </remarks>
    event Action CoreReady;

    // ---- FR-API-02 player data -----------------------------------------------

    bool IsLoaded(ulong steamId);
    bool IsLoaded(IPlayer player);

    int GetPoints(ulong steamId);
    int GetPoints(IPlayer player);

    /// <summary>Sets the player's total outright, never below <c>Rank.MinPoints</c>.</summary>
    /// <remarks>
    /// Applies at once on the game thread. Called from another thread it applies on the next game tick — an
    /// <c>IPlayer</c> overload only while that player still holds their slot — so a read straight after the call
    /// still sees the old total, and <c>PointsChanged</c> and <c>RankChanged</c> follow on the game thread.
    /// </remarks>
    void SetPoints(ulong steamId, int points, string reason);

    /// <inheritdoc cref="SetPoints(ulong, int, string)"/>
    void SetPoints(IPlayer player, int points, string reason);

    /// <summary>Adds <paramref name="amount"/> as given — no gate, multiplier or scaling — floored at <c>Rank.MinPoints</c>.</summary>
    /// <remarks>Threading as <see cref="SetPoints(ulong, int, string)"/>.</remarks>
    void AddPoints(ulong steamId, int amount, string reason, bool showMessage = true);

    /// <inheritdoc cref="AddPoints(ulong, int, string, bool)"/>
    void AddPoints(IPlayer player, int amount, string reason, bool showMessage = true);

    RanksPlayerSnapshot? GetSnapshot(ulong steamId);
    RanksPlayerSnapshot? GetSnapshot(IPlayer player);

    /// <summary>The tag core would write, or null when clan tags are off or the player is absent.</summary>
    /// <remarks>Read-only and side-effect free, so a tag arbiter can ask without fighting core.</remarks>
    string? GetDesiredClanTag(ulong steamId);

    // ---- FR-API-03 statistics ------------------------------------------------

    /// <summary>Reads one of core's fixed counters by key, e.g. <c>core.kills</c>.</summary>
    /// <remarks>
    /// Stat keys keep the <c>core.</c> prefix that point-event keys dropped, so the event key
    /// <c>kill</c> here is not an error and returns 0 forever. Read-only by design; core is the
    /// single writer.
    /// </remarks>
    long GetStat(ulong steamId, string statKey);

    // ---- FR-API-04 leaderboards and lifecycle --------------------------------

    Task<IReadOnlyList<RanksLeaderboardEntry>> GetTopAsync(string statKey = "points", int limit = 10);
    Task<int> GetPositionAsync(ulong steamId, string statKey = "points");
    Task<int> GetTotalRankedAsync();

    /// <remarks>Every event below is raised on the game thread, and that is part of the contract.</remarks>
    event Action<RanksPlayerSnapshot> PlayerDataLoaded;

    event Action<RanksPointsChangingArgs> PointsChanging;

    /// <summary>Raised after a player's total changed.</summary>
    /// <remarks>
    /// Also raised for core's <c>!setpoints</c> naming the total the player already has — reason <c>admin.set</c>,
    /// <c>Delta</c> 0 and <c>OldPoints</c> equal to <c>NewPoints</c> — so a rating module still takes the admin's
    /// value in (FR-CMD-03). No other unchanged total is reported.
    /// </remarks>
    event Action<RanksPointsChangedArgs> PointsChanged;

    event Action<RanksRankChangedArgs> RankChanged;
    event Action<RanksPlayerSnapshot> PlayerDataSaved;

    /// <summary>Purged SteamIDs, so a module can sweep its own tables in the same pass (FR-DB-08).</summary>
    event Action<IReadOnlyList<ulong>> PlayersPurged;

    /// <summary>Core's quiet window, raised after core's own maintenance completes (FR-DB-09).</summary>
    /// <remarks>
    /// Raised once after the pass a server start forces, then after each later pass, which runs only on an empty
    /// server, every <c>Database.MaintenanceIntervalHours</c> while that is above 0. A load of core into a running
    /// server forces no pass. Core does not wait for subscribers, so hand heavy work to a background task.
    /// </remarks>
    event Action MaintenanceDue;

    // ---- FR-API-11 kill arbitration ------------------------------------------

    /// <summary>Registers the arbiter core consults before awarding a kill, or null to clear it.</summary>
    /// <remarks>
    /// At most one is active: a second registration replaces the first, the same single-authority
    /// constraint as absolute scoring (FR-PTS-00). While a module holds the scoring claim core still
    /// asks the arbiter about every kill, but pays nothing it claims (FR-API-14).
    /// </remarks>
    void SetKillArbiter(IRanksKillArbiter? arbiter);

    // ---- FR-API-05 gates and settings ----------------------------------------

    /// <summary>Whether core would award points right now: <c>UseCoreRankingLogic</c> on, no module holding the scoring claim, and play counted.</summary>
    bool ArePointsEnabled { get; }

    /// <summary>Whether core counts play at all: <c>MinPlayers</c> met, not a warmup <c>WarmupPoints</c> excludes, and no bots-only team while <c>BotsEnabled</c> is off.</summary>
    /// <remarks>
    /// While false core awards no points of its own and records no counter except playtime, which
    /// measures time connected rather than play (FR-PTS-03); a module's <c>AddPoints</c> and
    /// <c>SetPoints</c> still land. A module keeping its own statistics asks per event;
    /// core decides once per server tick, so the module gets the answer core used for the same
    /// event. Ignores <c>UseCoreRankingLogic</c> and the scoring claim. A single interaction with a
    /// bot is still the caller's call, made with <c>BotsEnabled</c>.
    /// </remarks>
    bool IsPlayCounted { get; }

    /// <summary>A fresh snapshot per call, so it tracks live config reloads.</summary>
    /// <remarks>
    /// <c>UseCoreRankingLogic</c> is what the operator configured, which a scoring claim overrides
    /// without rewriting. Whether core is scoring is <see cref="ArePointsEnabled"/> and <see cref="ScoringOwner"/>.
    /// </remarks>
    RanksPointSettings GetPointSettings();

    // ---- FR-API-06 database coordination -------------------------------------

    string ConnectionName { get; }

    /// <summary>Applies the operator's prefix and suffix to a base name such as <c>ranks_players</c>.</summary>
    string ResolveTableName(string baseName);

    /// <summary>Hands an assembly's migrations to core, which owns the runner.</summary>
    /// <remarks>
    /// Call from the handshake, not <c>Load</c> — there is no API yet in <c>Load</c>. Idempotent: core dedupes on
    /// (assembly, pluginId) and migrates each once per load of core. At server start that happens once every plugin has
    /// loaded. Later — a module loaded or updated on a running server, or every module after core itself reloads, which
    /// keeps no record of earlier runs — core migrates it in the background, one module at a time, starting as this call
    /// returns. Until that finishes, a table it creates may not exist yet, so a module retries a write that fails.
    /// </remarks>
    void RegisterMigrations(Assembly assembly, string pluginId);

    // ---- FR-CMD-04 menu composition ------------------------------------------

    /// <summary>Adds one line to core's menu, under the section the entry names.</summary>
    /// <remarks>
    /// Call from the handshake, after core is ready. Entries are keyed on
    /// (<paramref name="moduleId"/>, <c>Section</c>, <c>Label</c>), so re-registering the same row
    /// after a config reload replaces it rather than doubling it.
    ///
    /// Core never names a module type: it holds the delegate and a string, both declared here.
    /// That is also why <see cref="UnregisterMenuEntries"/> is not optional — an entry outliving
    /// its module is a delegate into an unloaded assembly.
    /// </remarks>
    void RegisterMenuEntry(string moduleId, RanksMenuEntry entry);

    /// <summary>Drops every entry a module registered. Idempotent.</summary>
    void UnregisterMenuEntries(string moduleId);

    // ---- FR-API-07 scoreboard display overlay --------------------------------

    /// <summary>Overlays what the scoreboard shows for one player, leaving stored points alone.</summary>
    /// <remarks>
    /// Applies at once on the game thread; called from another thread, on the next game tick, and only while the
    /// player still holds their slot. Every display surface core owns — the rank icon, the mode it is drawn
    /// in, the clan tag and the synced score — is resolved from the overlay, so a module never has
    /// to write those fields itself and never fights core for the tag (FR-API-02).
    /// <para>
    /// Session-scoped: dropped when the player disconnects, never persisted. A module that wants it
    /// to survive a reconnect re-applies it from <c>PlayerDataLoaded</c>.
    /// </para>
    /// </remarks>
    void SetDisplayOverride(IPlayer player, RanksDisplayOverride overlay);

    /// <summary>Removes the player's overlay.</summary>
    /// <remarks>Threading as <see cref="SetDisplayOverride"/>.</remarks>
    void ClearDisplayOverride(IPlayer player);

    RanksDisplayOverride? GetDisplayOverride(IPlayer player);

    // ---- FR-API-09 explicit persistence --------------------------------------

    /// <summary>False when the SteamID is not tracked; a failed load leaves a player untracked.</summary>
    Task<bool> SavePlayerAsync(ulong steamId);

    /// <summary>Returns records committed. Coalesces — a concurrent call joins the in-flight pass.</summary>
    Task<int> SaveAllAsync();

    // ---- FR-API-12 display data ----------------------------------------------

    /// <summary>The operator's ladder, lowest tier first. A fresh list per call, so it tracks reloads.</summary>
    IReadOnlyList<RanksTier> GetLadder();

    /// <summary>The rank icon the scoreboard draws for a point total in the configured <c>Scoreboard.RankMode</c>.</summary>
    /// <remarks>Answers for a number, not a player, so display overlays do not apply.</remarks>
    RanksRankIcon GetRankIcon(int points);

    /// <summary>A player's snapshot online or not: the resident record when there is one, else the stored row.</summary>
    /// <remarks>Null when the player has never been seen. Reads the database for an offline player.</remarks>
    Task<RanksPlayerSnapshot?> LoadSnapshotAsync(ulong steamId);

    // ---- FR-API-13 periods (virtual purge) ------------------------------------

    /// <summary>The period every write lands in now. See <see cref="RanksPeriods"/> for what the id means.</summary>
    /// <remarks>
    /// Changes only inside core's rollover, never mid-call, so a module stamping its own rows with it
    /// agrees with core about which period a write belongs to. While <see cref="ArePeriodsEnabled"/>
    /// is false it stays on the latest period the data holds — <see cref="RanksPeriods.Initial"/>
    /// on an install that never enabled them.
    /// </remarks>
    long CurrentPeriod { get; }

    /// <summary>True while <c>Database.VirtualPurgeMode</c> is <c>1</c> (monthly), or <c>VirtualPurgeDays</c> is above zero.</summary>
    bool ArePeriodsEnabled { get; }

    /// <summary>When the current period closes, in UTC, or null while periods are disabled.</summary>
    DateTime? CurrentPeriodEndsAt { get; }

    /// <summary>Raised once core has rolled every resident record into the new period.</summary>
    /// <remarks>
    /// A module holding unwritten counts stamps them with <c>OldPeriod</c> — only while
    /// <see cref="ArePeriodsEnabled"/>, because core also follows another server sharing the database
    /// into a newer period with its own periods off. A module keeping its own authoritative value per
    /// player (a rating) must drop it here, or its next write restores last period's number over core's fresh one.
    /// </remarks>
    event Action<RanksPeriodChangedArgs> PeriodChanged;

    /// <summary>Raised after a player's current period was wiped. Past periods are kept.</summary>
    /// <remarks>A module should clear its current-period figures for the player in the same pass.</remarks>
    event Action<RanksPlayerResetArgs> PlayerReset;

    /// <summary>Every period holding data, newest first, always including <see cref="CurrentPeriod"/>.</summary>
    Task<IReadOnlyList<long>> GetPeriodsAsync();

    /// <summary>One period's leaderboard, or every period summed with <see cref="RanksPeriods.AllTime"/>.</summary>
    /// <remarks>The all-time <c>points</c> figure is what a player would hold had no period ever reset them.</remarks>
    Task<IReadOnlyList<RanksLeaderboardEntry>> GetTopAsync(string statKey, int limit, long period);

    /// <summary>A player's position in one period, or all time. 0 when they hold no figures there.</summary>
    Task<int> GetPositionAsync(ulong steamId, string statKey, long period);

    /// <summary>A player's figures for one period, or all time. Null when they hold none there.</summary>
    Task<RanksPlayerSnapshot?> LoadSnapshotAsync(ulong steamId, long period);

    // ---- FR-API-14 scoring claim ---------------------------------------------

    /// <summary>Hands scoring to a rating module: core awards no points of its own while the claim is held.</summary>
    /// <remarks>
    /// Overrides <c>Rank.UseCoreRankingLogic</c> without touching the operator's file, and leaves
    /// <c>AddPoints</c> and <c>SetPoints</c> raw. True again for the holder, so claim from every handshake:
    /// a core reload builds a new API that holds no claim. False while another module holds it, and always
    /// for a blank <paramref name="moduleId"/>.
    /// </remarks>
    bool ClaimScoring(string moduleId);

    /// <summary>Hands scoring back to core's own switch. A no-op unless <paramref name="moduleId"/> holds the claim.</summary>
    /// <remarks>Call it from <c>Unload</c>: a claim nobody releases keeps core silent until core itself reloads.</remarks>
    void ReleaseScoring(string moduleId);

    /// <summary>The module holding the scoring claim, or null while core's own switch decides.</summary>
    string? ScoringOwner { get; }
}

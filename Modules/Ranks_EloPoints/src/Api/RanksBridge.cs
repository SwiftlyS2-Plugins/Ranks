using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.EloPoints.Match;
using Ranks.EloPoints.Rating;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/> or one of the flattened records below. Returning a
/// <c>RanksPlayerSnapshot</c> or <c>RanksPointSettings</c> would put a contract type in a
/// signature outside this file, which is the same hazard one indirection further out.
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// <c>_api.PlayersPurged -= handler</c> in Unload is the classic survivor.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly RatingStore _store;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining.
    private object? _api;
    private object? _module;
    private volatile bool _periodsUnsupported;

    /// <summary>The period every row of an install that never enabled periods sits in.</summary>
    private const long InitialPeriod = RanksPeriods.Initial;

    /// <summary>This plugin's <c>[PluginMetadata]</c> id, which core knows the module and its claim by.</summary>
    internal const string ModuleId = "Ranks_EloPoints";

    public RanksBridge(ISwiftlyCore core, RatingStore store, ILogger logger)
    {
        _core = core;
        _store = store;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Where this module stands on core's scoring claim, as of the last handshake (FR-API-14).</summary>
    public ScoringClaim ScoringClaim { get; private set; }

    /// <summary>The module that held the claim when this one was refused it.</summary>
    public string? ScoringHolder { get; private set; }

    /// <summary>Raised once core reports ready, so the plugin can run its one-shot checks.</summary>
    public event Action? CoreReady;

    /// <summary>Set by the module so the tracker can be reached from core's lifecycle events.</summary>
    public MatchTracker? Tracker { get; set; }

    /// <summary>Set by the plugin so the published API can be given core's resolved table name.</summary>
    public EloPointsApi? PublishedApi { get; set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager)
    {
        try
        {
            // HasSharedInterface matches on the key string; TryGetSharedInterface resolves by Type.
            // The two disagreeing is the one symptom of a second Ranks.Contract.dll — core publishes
            // under its IRanksApi and this module asks for a different one — and the typed call alone
            // reports it as a bare false, indistinguishable from core not being installed.
            var published = interfaceManager.HasSharedInterface(IRanksApi.InterfaceKey);

            // Fail-closed on the miss: leaving a stale reference behind would point at a disposed
            // provider after the next handshake rebuild.
            if (!interfaceManager.TryGetSharedInterface<IRanksApi>(IRanksApi.InterfaceKey, out var api) || api is null)
            {
                if (published)
                    _logger.LogWarning(
                        "Ranks_EloPoints: '{Key}' is published but did not resolve to this module's IRanksApi. " +
                        "Ranks.Contract.dll is loaded twice — it must ship only in Ranks/resources/exports.",
                        IRanksApi.InterfaceKey);

                _api = null;
                IsAvailable = false;

                return false;
            }

            _api = api;
            IsAvailable = true;

            Attach(api);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_EloPoints: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>
    /// Idempotent — the handshake re-runs on every plugin load and unload. Scoring is claimed first, so
    /// the one-shot ready check <c>Register</c> may raise already knows how the claim went.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        ClaimScoring();

        var module = ModuleFor(api);

        module.Register();
        module.HandOverMigrations(typeof(RanksBridge).Assembly);
    }

    /// <summary>The module bound to <paramref name="api"/>, built afresh when a core reload has handed over a new instance.</summary>
    /// <remarks>
    /// A module's <c>Api</c> is fixed at construction, and a core reload clears the events the old instance fed —
    /// kept, the module would stop hearing resets, new periods and admin changes. Its state lives in this plugin
    /// (tracker, store), so nothing is lost, and <c>Register</c> replays <c>OnCoreReady</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EloPointsModule ModuleFor(IRanksApi api)
    {
        if (_module is EloPointsModule current && current.IsBoundTo(api)) return current;

        if (_module is EloPointsModule stale) Retire(stale);

        var module = new EloPointsModule(api, _core, this, _store, () => CoreReady?.Invoke());
        _module = module;

        return module;
    }

    /// <summary>Unsubscribes a module from the core instance that unloaded under it.</summary>
    /// <remarks>Guarded: that core is gone, so a failure here costs nothing and must not stop the rebind.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Retire(EloPointsModule stale)
    {
        try
        {
            stale.Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_EloPoints: could not unsubscribe from the Ranks instance that unloaded.");
        }
    }

    /// <summary>Takes scoring from core, so core awards none of its own points while this module runs (FR-API-14).</summary>
    /// <remarks>
    /// Through the interface this handshake bound, never the module's, which still holds the unloaded core's until
    /// <see cref="ModuleFor"/> rebuilds it; a new instance holds no claim until this runs again. A Ranks older than
    /// 1.5.0 has no claim and throws MissingMethodException from the call below, which surfaces here; its switch alone decides.
    /// </remarks>
    private void ClaimScoring()
    {
        if (ScoringClaim == ScoringClaim.Unsupported) return;

        try
        {
            ScoringClaim = RequestClaim(out var holder);
            ScoringHolder = holder;
        }
        catch (MissingMethodException)
        {
            ScoringClaim = ScoringClaim.Unsupported;
        }
    }

    /// <summary>Asks core for the claim, naming the holder when it refuses. Unchanged while core is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ScoringClaim RequestClaim(out string? holder)
    {
        holder = null;

        if (_api is not IRanksApi api) return ScoringClaim;
        if (api.ClaimScoring(ModuleId)) return ScoringClaim.Held;

        holder = api.ScoringOwner;
        return ScoringClaim.Refused;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        if (ScoringClaim == ScoringClaim.Held) ReleaseScoring();

        if (_module is EloPointsModule module) module.Unregister();

        _module = null;
        _api = null;
        IsAvailable = false;
    }

    /// <summary>Hands scoring back to core's own switch as this module goes.</summary>
    /// <remarks>
    /// Kept out of <see cref="Unwire"/>'s body and reached only after a granted claim: JIT-compiling it against a
    /// Ranks without the member fails, and inside Unwire that would skip the unsubscribe after it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseScoring() => (_api as IRanksApi)?.ReleaseScoring(ModuleId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int? GetPoints(ulong steamId) => _api is IRanksApi api ? api.GetPoints(steamId) : null;

    /// <summary>Whether core holds the player's record yet. False while core is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool IsLoaded(ulong steamId) => _api is IRanksApi api && api.IsLoaded(steamId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetPoints(ulong steamId, int points, string reason) => (_api as IRanksApi)?.SetPoints(steamId, points, reason);

    /// <remarks>
    /// Explicit rather than waiting for core's periodic pass: the record is evicted once saved and
    /// the next pass may be minutes away (FR-MOD-05 rule 5).
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SavePlayer(ulong steamId) => _ = (_api as IRanksApi)?.SavePlayerAsync(steamId);

    /// <summary>Core's counters as a flattened record, or null when core is absent or the player is untracked.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PerfCounters? GetCounters(ulong steamId)
    {
        if (_api is not IRanksApi api || api.GetSnapshot(steamId) is not { } snapshot) return null;

        return new PerfCounters(
            snapshot.Kills,
            snapshot.Deaths,
            snapshot.Assists,
            snapshot.RoundWins,
            snapshot.BombsPlanted,
            snapshot.BombsDefused);
    }

    /// <summary>A fresh snapshot per call, so it tracks live config reloads rather than caching at load.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public CoreSettings? GetSettings()
    {
        if (_api is not IRanksApi api) return null;

        var settings = api.GetPointSettings();

        return new CoreSettings(
            settings.UseCoreRankingLogic,
            settings.StartPoints,
            settings.MinPoints,
            settings.MinPlayers,
            settings.WarmupPoints,
            settings.BotsEnabled);
    }

    /// <summary>The core table name, resolved with the operator's affixes.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ResolveTable(string baseName) => _api is IRanksApi api ? api.ResolveTableName(baseName) : null;

    /// <summary>Core's play gate, null when core is absent or not ready.</summary>
    /// <remarks>Throws MissingMethodException against a Ranks older than the member; the tracker catches it.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? IsPlayCounted() => _api is IRanksApi api ? api.IsPlayCounted : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ConnectionName() => _api is IRanksApi api ? api.ConnectionName : null;

    /// <summary>The period core writes to now, or null while core is unavailable.</summary>
    /// <remarks>
    /// Names no contract type itself, so any path may call it; the guard is inside. A Ranks older than
    /// 1.1.0 has no periods and throws MissingMethodException from the read below, which surfaces here;
    /// from then on this answers period 0, where every row of such an install sits, so the module runs
    /// exactly as 1.0.1 did. Safe off the game thread: core moves the value only inside its rollover.
    /// </remarks>
    public long? CurrentPeriod()
    {
        if (!IsAvailable) return null;
        if (_periodsUnsupported) return InitialPeriod;

        try
        {
            return ReadCurrentPeriod();
        }
        catch (MissingMethodException)
        {
            _periodsUnsupported = true;
            _logger.LogWarning(
                "Ranks_EloPoints: this Ranks version has no seasons, so ratings never restart and !resetrank does not " +
                "clear a rating. Update Ranks to 1.1.0 or newer.");

            return InitialPeriod;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long? ReadCurrentPeriod() => _api is IRanksApi api ? api.CurrentPeriod : null;
}

/// <summary>This module's hook into core's lifecycle.</summary>
/// <remarks>
/// Lives beside the bridge rather than in its own file because it also names contract types, and
/// the isolation rule above is "exactly one file", not "exactly one class".
/// </remarks>
internal sealed class EloPointsModule : RanksModuleBase
{
    /// <summary>The reason core's <c>!setpoints</c> writes; <c>!givepoints</c> and <c>!removepoints</c> write the two after it.</summary>
    private const string AdminSet = "admin.set";
    private const string AdminAdd = "admin.add";
    private const string AdminSubtract = "admin.subtract";

    private readonly RanksBridge _bridge;
    private readonly RatingStore _store;
    private readonly Action _onCoreReady;

    public EloPointsModule(IRanksApi api, ISwiftlyCore core, RanksBridge bridge, RatingStore store, Action onCoreReady)
        : base(api, core)
    {
        _bridge = bridge;
        _store = store;
        _onCoreReady = onCoreReady;
    }

    public override string ModuleId => RanksBridge.ModuleId;

    /// <summary>True when this module was built for <paramref name="api"/>.</summary>
    public bool IsBoundTo(IRanksApi api) => ReferenceEquals(Api, api);

    /// <summary>Hands this assembly's migrations to core.</summary>
    /// <remarks>
    /// Core dedupes on (assembly, pluginId), so calling it from every handshake — which re-runs on
    /// every other plugin's load and unload — is harmless.
    /// </remarks>
    public void HandOverMigrations(Assembly assembly) => RegisterMigrations(assembly);

    /// <remarks>
    /// Table names are resolved here rather than in Load: a module's Load runs before the
    /// handshake, so there is no API to ask at that point.
    /// </remarks>
    protected override void OnCoreReady()
    {
        var table = Table("ranks_elopoints");
        var history = Table("ranks_elopoints_history");

        _store.Configure(ConnectionName, table, history);
        _bridge.PublishedApi?.Configure(ConnectionName, table, history);
        _onCoreReady();
    }

    /// <remarks>
    /// Two jobs in one handler: capture the match-start counters for a mid-match joiner — starting the session of one
    /// whose SteamID was not known when they entered — and push a rating that was settled while the player was
    /// already gone back into core. Only a rating from the current period is pushed — core has just started this player there.
    /// </remarks>
    protected override void OnPlayerDataLoaded(RanksPlayerSnapshot player)
    {
        var counters = new PerfCounters(
            player.Kills,
            player.Deaths,
            player.Assists,
            player.RoundWins,
            player.BombsPlanted,
            player.BombsDefused);

        _bridge.Tracker?.OnPlayerDataLoaded(player.SteamId, counters, player.Points);

        if (_bridge.CurrentPeriod() is { } period
            && _store.TryGetCached(player.SteamId, period, out var rating)
            && (int)Math.Round(rating) != player.Points)
            Api.SetPoints(player.SteamId, (int)Math.Round(rating), "elopoints.resync");
    }

    /// <summary>Takes core's admin point commands into the player's rating for the current period.</summary>
    /// <remarks>
    /// Every other reason — another plugin's <c>AddPoints</c> or <c>SetPoints</c> included — is overwritten
    /// by the rating at the player's next resync or match end, because the rating is the one authority.
    /// </remarks>
    protected override void OnPointsChanged(RanksPointsChangedArgs e)
    {
        if (!IsAdminChange(e.Reason) || _bridge.CurrentPeriod() is not { } period) return;

        _bridge.Tracker?.AdoptAdminChange(
            new AdminPointChange(e.SteamId, e.OldPoints, e.NewPoints, e.Delta, Absolute: e.Reason == AdminSet),
            period,
            Api.GetPointSettings().MinPoints);
    }

    private static bool IsAdminChange(string reason) => reason is AdminSet or AdminAdd or AdminSubtract;

    /// <remarks>
    /// Core has already removed its own rows by the time this fires, so the module must not expect
    /// to look the player up.
    /// </remarks>
    protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _store.PurgeAsync(steamIds);

    /// <remarks>
    /// FR-DB-10. Core has already put every resident player back on StartPoints; keeping a rating
    /// from the closed period would write it straight back over that at the next match end.
    /// </remarks>
    protected override void OnPeriodChanged(RanksPeriodChangedArgs e)
    {
        _store.ForgetAll();
        _bridge.Tracker?.OnPeriodChanged(e.NewPeriod);
    }

    /// <remarks>
    /// Core has wiped the player's current period. Without this, the next match end writes the
    /// pre-reset rating, plus that match's result, straight back over the reset.
    /// </remarks>
    protected override void OnPlayerReset(RanksPlayerResetArgs e)
    {
        _ = _store.ResetAsync(e.SteamId, Api.CurrentPeriod);
        _bridge.Tracker?.OnPlayerReset(e.SteamId);
    }
}

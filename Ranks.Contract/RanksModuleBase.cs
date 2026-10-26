using System.Reflection;
using SwiftlyS2.Shared;

namespace Ranks.Contract;

/// <summary>Data and lifecycle base for a Ranks module.</summary>
/// <remarks>
/// Deliberately not a feature base: FR-CMD-04 closes core's menu to modules, so there is no
/// per-player toggle and no menu registration here. Acquisition and the JIT isolation stay in the
/// module's own BasePlugin — a constructor parameter typed <see cref="IRanksApi"/> means the
/// caller already holds one (FR-API-08).
/// </remarks>
public abstract class RanksModuleBase
{
    private bool _registered;
    private bool _coreReadyRaised;

    protected RanksModuleBase(IRanksApi api, ISwiftlyCore core)
    {
        Api = api;
        Core = core;
    }

    protected IRanksApi Api { get; }

    protected ISwiftlyCore Core { get; }

    /// <summary>The module's own <c>[PluginMetadata]</c> id — core derives the ledger from it.</summary>
    public abstract string ModuleId { get; }

    protected string ConnectionName => Api.ConnectionName;

    protected string Table(string baseName) => Api.ResolveTableName(baseName);

    /// <summary>Hands this module's migrations to core, which owns the runner (FR-DB-05).</summary>
    protected void RegisterMigrations(Assembly assembly) => Api.RegisterMigrations(assembly, ModuleId);

    /// <remarks>Every handler below is raised on the game thread, so an override may touch players directly.</remarks>
    protected virtual void OnCoreReady() { }

    protected virtual void OnPlayerDataLoaded(RanksPlayerSnapshot player) { }

    protected virtual void OnPlayerDataSaved(RanksPlayerSnapshot player) { }

    /// <remarks>
    /// Subscriber order across modules is undefined and each sees the previous one's Amount, so two
    /// modules scaling one award compose in an order nobody chose. Do not score here — call
    /// <c>AddPoints</c> with your own number.
    /// </remarks>
    protected virtual void OnPointsChanging(RanksPointsChangingArgs e) { }

    protected virtual void OnPointsChanged(RanksPointsChangedArgs e) { }

    protected virtual void OnRankChanged(RanksRankChangedArgs e) { }

    protected virtual void OnPlayersPurged(IReadOnlyList<ulong> steamIds) { }

    protected virtual void OnMaintenanceDue() { }

    /// <summary>Subscribes and resolves readiness. Idempotent — call it from <c>Load</c>.</summary>
    /// <remarks>
    /// The guard is what the handshake makes necessary: it re-runs on every plugin load and unload,
    /// so an unguarded <c>+=</c> is one duplicate subscription per rebuild.
    /// </remarks>
    public void Register()
    {
        if (_registered) return;
        _registered = true;

        Api.PlayerDataLoaded += OnPlayerDataLoaded;
        Api.PlayerDataSaved += OnPlayerDataSaved;
        Api.PointsChanging += OnPointsChanging;
        Api.PointsChanged += OnPointsChanged;
        Api.RankChanged += OnRankChanged;
        Api.PlayersPurged += OnPlayersPurged;
        Api.MaintenanceDue += OnMaintenanceDue;

        if (Api.IsCoreReady) RaiseCoreReady();
        else Api.CoreReady += RaiseCoreReady;
    }

    /// <summary>Unsubscribes. Idempotent — call it from <c>Unload</c>.</summary>
    public void Unregister()
    {
        if (!_registered) return;
        _registered = false;

        Api.PlayerDataLoaded -= OnPlayerDataLoaded;
        Api.PlayerDataSaved -= OnPlayerDataSaved;
        Api.PointsChanging -= OnPointsChanging;
        Api.PointsChanged -= OnPointsChanged;
        Api.RankChanged -= OnRankChanged;
        Api.PlayersPurged -= OnPlayersPurged;
        Api.MaintenanceDue -= OnMaintenanceDue;
        Api.CoreReady -= RaiseCoreReady;
    }

    /// <remarks>Guarded separately from <see cref="Register"/>: a re-register after core came up must not re-run it.</remarks>
    private void RaiseCoreReady()
    {
        if (_coreReadyRaised) return;
        _coreReadyRaised = true;

        OnCoreReady();
    }
}

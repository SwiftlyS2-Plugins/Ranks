using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Ranks.EloPoints.Api;
using Ranks.EloPoints.Config;
using Ranks.EloPoints.Match;
using Ranks.EloPoints.Rating;
using Ranks.EloPoints.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Plugins;

namespace Ranks.EloPoints;

[PluginMetadata(
    Id = "Ranks_EloPoints",
    Version = "1.2.0",
    Name = "Ranks EloPoints",
    Author = "Shmitzas",
    Description = "Faceit-style Elo rating for the Ranks plugin. Replaces core scoring.")]
public sealed class RanksEloPoints : BasePlugin
{
    private ILogger _logger = null!;
    private IDisposable? _reload;
    private EloConfig _config = null!;
    private RatingStore _store = null!;
    private MatchTracker _tracker = null!;
    private RanksBridge _bridge = null!;
    private EloPointsApi _api = null!;
    private bool _contractLoadFailed;

    /// <summary>True from a load into a running server until Ranks is first bound and the players already here adopted.</summary>
    private bool _adoptOnBind;

    private CancellationTokenSource? _endWatchdog;

    public RanksEloPoints(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks_EloPoints");

        LoadConfiguration();

        _store = new RatingStore(Core, _logger);
        _bridge = new RanksBridge(Core, _store, _logger);
        _tracker = new MatchTracker(Core, _bridge, _store, _config, _logger);
        _bridge.Tracker = _tracker;
        _bridge.CoreReady += OnRanksReady;
        _api = new EloPointsApi(Core, _tracker, _bridge, _logger);
        _bridge.PublishedApi = _api;

        // Exactly once. Registering twice double-counts every round.
        Core.Registrator.Register(_tracker);

        Core.Event.OnClientPutInServer += OnPutInServer;
        Core.Event.OnClientDisconnected += OnDisconnected;
        Core.Event.OnMapUnload += OnMapUnload;

        StartEndWatchdog();

        _adoptOnBind = LoadsIntoRunningServer(hotReload);
    }

    /// <remarks>
    /// Acquired here rather than in Load: the shared interface does not exist yet in Load, which
    /// is also why RegisterMigrations cannot live there.
    /// </remarks>
    public override void UseSharedInterface(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    /// <remarks>
    /// The catch has to be here rather than inside the bridge. A missing Ranks.Contract.dll fails
    /// when the CLR JITs TryAcquire, which surfaces in this frame before the bridge's own catch can
    /// run — so a catch inside it would never see this. Reported at Debug because OnAllPluginsLoaded
    /// owns the operator-facing message.
    /// </remarks>
    private void Acquire(IInterfaceManager interfaceManager)
    {
        try
        {
            _bridge.TryAcquire(interfaceManager);
        }
        catch (Exception ex)
        {
            _contractLoadFailed = true;
            _logger.LogDebug(ex, "Ranks_EloPoints: the Ranks contract could not be loaded.");
        }
    }

    /// <remarks>
    /// Published unconditionally, and without the JIT isolation the Ranks API needs: this module
    /// hard-requires the assembly it publishes from, exactly as core does with Ranks.Contract.
    /// </remarks>
    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<IEloPointsApi, EloPointsApi>(IEloPointsApi.InterfaceKey, _api);

    /// <remarks>
    /// Absence is reported from here, not from the binder: the binder runs on every rebuild and
    /// would spam, while this runs once, at server start, and is the only point where field state is
    /// authoritative. A hot load or reload never reaches it.
    /// </remarks>
    public override void OnAllPluginsLoaded()
    {
        if (_bridge.IsAvailable) return;

        // Two different faults with one symptom: core absent, or present but its contract
        // unloadable from here. Saying "not present" for the second sends the operator looking
        // for a plugin that is sitting right there.
        if (_contractLoadFailed)
        {
            _logger.LogWarning(
                "Ranks_EloPoints: Ranks.Contract could not be loaded, so this module is idle. " +
                "Check that Ranks ships resources/exports/Ranks.Contract.dll and that this module does not.");

            return;
        }

        _logger.LogWarning(
            "Ranks_EloPoints: the Ranks plugin is not present, so this module is idle. " +
            "No ratings are being recorded and no tables were created.");
    }

    public override void Unload()
    {
        _endWatchdog?.Cancel();
        _endWatchdog = null;

        Core.Event.OnClientPutInServer -= OnPutInServer;
        Core.Event.OnClientDisconnected -= OnDisconnected;
        Core.Event.OnMapUnload -= OnMapUnload;

        _tracker?.DiscardMatch("the plugin is unloading");

        // Same JIT hazard as Acquire: Unwire names contract types, so a missing Ranks.Contract.dll
        // throws into this frame rather than into Unwire's own body.
        try
        {
            _bridge?.Unwire();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_EloPoints: the Ranks contract could not be unwired.");
        }

        _reload?.Dispose();
    }

    /// <remarks>
    /// Bound off <c>Core.Configuration.Manager</c>, the root the framework already owns and
    /// watches — no ServiceCollection and no IOptionsMonitor needed to get a live reload.
    /// </remarks>
    private void LoadConfiguration()
    {
        Core.Configuration
            // addMissingKeys: a plugin upgrade inserts new keys into an existing file with their
            // defaults, leaving the operator's values and formatting alone.
            .InitializeJsonWithModel<EloConfig>("config.jsonc", "EloPoints", true)
            // The full path matters: AddJsonFile resolves relative to the process working
            // directory, not the plugin folder.
            .Configure(builder =>
                builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true));

        _config = BindConfig();
        WarnOnUnrecognisedMode(_config);

        _reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
        {
            var incoming = BindConfig();

            WarnOnUnrecognisedMode(incoming);

            _config = incoming;
            _tracker?.UpdateConfig(incoming);
        });
    }

    private EloConfig BindConfig()
    {
        var bound = Core.Configuration.Manager.GetSection("EloPoints").Get<EloConfig>() ?? new EloConfig();
        bound.Clamp();

        return bound;
    }

    private void WarnOnUnrecognisedMode(EloConfig config)
    {
        if (!config.ModeUnrecognised) return;

        _logger.LogWarning(
            "Ranks_EloPoints: Rating.Mode '{Mode}' is not recognised, so RoundAccumulation is in use. " +
            "Valid values are RoundAccumulation and Classic.",
            config.Rating.Mode);
    }

    /// <remarks>
    /// The backstop for EventCsWinPanelMatch. The failure it guards is that nothing is ever
    /// written for any match, which is the one outcome that must not be silent — so it polls
    /// rather than trusting a single event. Settling is idempotent, so both firing is harmless.
    ///
    /// DelayAndRepeat rather than Repeat: RepeatBySeconds fires its first callback on the next
    /// scheduler tick, which lands inside CS2's map-load entity shuffle, and this callback reads
    /// native game state.
    /// </remarks>
    private void StartEndWatchdog() => _endWatchdog = Core.Scheduler.DelayAndRepeatBySeconds(5, 5, () =>
    {
        if (_bridge.IsAvailable && _tracker.MatchHasEnded()) _tracker.Settle();
    });

    private void OnPutInServer(IOnClientPutInServerEvent e)
    {
        if (Core.PlayerManager.GetPlayer(e.PlayerId) is { } player) _tracker.Adopt(player);
    }

    /// <remarks>
    /// Marks the session absent rather than dropping it: whether this is an abandon is only
    /// knowable at match end, and a reconnect must resume the same accrual.
    /// </remarks>
    private void OnDisconnected(IOnClientDisconnectedEvent e)
    {
        if (Core.PlayerManager.GetPlayer(e.PlayerId) is { SteamID: not 0 } player) _tracker.Release(player.SteamID);
    }

    /// <remarks>A match that never reaches a win panel pays nobody; nothing was written, so there is nothing to undo.</remarks>
    private void OnMapUnload(IOnMapUnloadEvent e) => _tracker.DiscardMatch("the map is changing");

    /// <summary>Whether this load lands on a server already running, which <paramref name="hotReload"/> alone does not say.</summary>
    /// <remarks>
    /// SwiftlyS2's plugin API — <c>IPluginManager.LoadPlugin</c> and <c>ReloadPlugin</c> — passes it false on a running
    /// server. Plugins load at server start before the first map, so a map being up is what tells the two apart.
    /// </remarks>
    private bool LoadsIntoRunningServer(bool hotReload) => hotReload || MapIsLoaded();

    /// <summary>Whether the engine has a map up; false when that cannot be read.</summary>
    /// <remarks><c>GlobalVars</c> throws until the first map, and unguarded in Load that throw aborts the load.</remarks>
    private bool MapIsLoaded()
    {
        try
        {
            return Core.Engine.GlobalVars.MaxClients > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs once per load of Ranks: reports where the scoring claim stands, and adopts the players already here the first time.</summary>
    private void OnRanksReady()
    {
        ReportScoringClaim();
        AdoptConnectedPlayersOnce();
    }

    /// <summary>Adopts everyone already connected, the first time Ranks is bound after this module loaded into a running server.</summary>
    /// <remarks>
    /// Not from Load: a session needs Ranks, which no load path has bound by then. Once per load of this module, so a
    /// Ranks reload replaying the ready check cannot void a match already being rated. A tick later, on the game
    /// thread, because the handshake that raises this runs on a pool thread under SwiftlyS2's file watcher.
    /// </remarks>
    private void AdoptConnectedPlayersOnce()
    {
        if (!_adoptOnBind) return;

        _adoptOnBind = false;
        Core.Scheduler.NextTick(_tracker.AdoptConnectedPlayers);
    }

    /// <remarks>
    /// Ungated by DetailedLogging: it fires once per load of Ranks, and it is the only signal between a
    /// misconfigured server and a ladder full of nonsense. Silent while this module holds the
    /// scoring claim — core logs the switch it overrode itself.
    /// </remarks>
    private void ReportScoringClaim()
    {
        switch (_bridge.ScoringClaim)
        {
            case ScoringClaim.Refused:
                ReportRefusedClaim();
                break;
            case ScoringClaim.Unsupported:
                WarnIfCoreStillScores();
                break;
        }
    }

    /// <remarks>Here rather than at the claim, which every handshake repeats.</remarks>
    private void ReportRefusedClaim() =>
        _logger.LogError(
            "Ranks_EloPoints: {Holder} already owns scoring, so both modules write every player's points and " +
            "neither number means anything. Remove one of them.",
            _bridge.ScoringHolder ?? "another module");

    /// <remarks>
    /// Only against a Ranks older than 1.5.0, which cannot hand scoring over. It warns rather than
    /// overriding — core's config belongs to the operator.
    /// </remarks>
    private void WarnIfCoreStillScores()
    {
        if (_bridge.GetSettings() is not { UseCoreRankingLogic: true }) return;

        _logger.LogWarning(
            "Ranks_EloPoints: core's Rank.UseCoreRankingLogic is true, so core is still awarding points " +
            "per kill while this module writes a rating over the top. Neither number is meaningful. " +
            "Set Rank.UseCoreRankingLogic to false in core's config.jsonc, or update Ranks to 1.5.0 or newer, " +
            "which turns core's own scoring off by itself while this module runs.");
    }
}

using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Commands;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Logging;
using Ranks.Menus;
using Ranks.Players;
using Ranks.Ranking;
using Ranks.Scoreboard;
using Ranks.Scoring;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace Ranks;

[PluginMetadata(
    Id = "Ranks",
    Version = "1.5.0",
    Name = "Ranks",
    Author = "Shmitzas",
    Description = "Extensive ranks and playtime data collection plugin.")]
public sealed partial class Ranks : BasePlugin
{
    /// <remarks>Bounded so a hung database cannot hold the server in shutdown indefinitely.</remarks>
    private static readonly TimeSpan UnloadFlushBudget = TimeSpan.FromSeconds(8);

    /// <remarks>
    /// Bounded like the flush, which it follows and which the run overlaps. A module migration runs in the background
    /// for under a second after a load into a running server, so only a reload about that quick waits at all.
    /// </remarks>
    private static readonly TimeSpan UnloadMigrationBudget = TimeSpan.FromSeconds(8);

    /// <remarks>
    /// The handshake after a load into a running server normally finishes well inside the delay, and the warning still
    /// lands beside it in the log. Module migrations run in the background, so they do not hold it up.
    /// </remarks>
    private const float HotLoadCheckDelaySeconds = 5f;

    /// <summary>Tables core reads, with the columns a missed migration would leave out.</summary>
    private static readonly (string Table, string Columns)[] RequiredReads =
    [
        ("ranks_players", "steam_id, headshots, hits, misses, period, playtime"),
        ("ranks_settings", "steam_id"),
        ("ranks_players_history", "steam_id, period, playtime"),
    ];

    private ILogger _logger = null!;
    private ConfigHost<RanksConfig> _config = null!;
    private ConfigHost<PointsConfig> _points = null!;
    private ConfigHost<LadderConfig> _ladderConfig = null!;
    private ConfigHost<CommandsConfig> _commandsConfig = null!;
    private DetailLog _detail = null!;
    private TableNames _tables = null!;
    private DatabaseConnection _database = null!;
    private SchemaMigrator _migrator = null!;
    private PlayerStore _store = null!;
    private PlayerRegistry _registry = null!;
    private RankLadder _ladder = null!;
    private RanksEventHub _events = null!;
    private PlayerSessions _sessions = null!;
    private AwardPipeline _awards = null!;
    private ScoringHandlers _scoring = null!;
    private ScoreboardService _scoreboard = null!;
    private RankRevealer _revealer = null!;
    private PlayerReset _reset = null!;
    private MenuEntryRegistry _menuEntries = null!;
    private RankMenus _menus = null!;
    private CommandRegistry _commands = null!;
    private RanksApi _api = null!;
    private CancellationTokenSource? _hotLoadCheck;

    /// <summary>What Load decided (<see cref="LoadsIntoRunningServer"/>): true for a load into a running server.</summary>
    private bool _loadedIntoRunningServer;

    /// <summary>True once SwiftlyS2 has raised OnAllPluginsLoaded, which it does only while it starts.</summary>
    private bool _allPluginsLoaded;

    /// <remarks>
    /// Stays empty: the engine is not up yet, and anything touching it here aborts plugin
    /// loading (implementation guide §2.1).
    /// </remarks>
    public Ranks(ISwiftlyCore core) : base(core)
    {
    }

    /// <remarks>
    /// The API instance is built in Load and only published here. AddSharedInterface throws on a
    /// duplicate key, but the rebuild disposes the manager before re-running registration, so
    /// re-registering is safe.
    /// </remarks>
    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<IRanksApi, RanksApi>(IRanksApi.InterfaceKey, _api);

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    /// <remarks>
    /// A load into a running server (<see cref="LoadsIntoRunningServer"/>) lands mid-round with players connected: it
    /// adopts them, re-sends the rank reveal they were sent long ago, migrates modules as they register, and runs no
    /// purge, because a reload is how an operator deploys a fix mid-session (FR-DB-09). One that finds no map is taken
    /// for a server start until its first tick says otherwise (<see cref="ConfirmServerStart"/>).
    /// </remarks>
    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks");

        _loadedIntoRunningServer = LoadsIntoRunningServer(hotReload);

        LoadConfiguration();
        OpenDatabase();
        BuildServices();

        var schemaError = MigrateSchema();
        var status = new SchemaProbe(_database, _tables, _logger).Probe(RequiredReads, schemaError);
        _sessions.SchemaUsable = status.Usable && _sessions.TryInitializePeriod();

        if (_sessions.SchemaUsable)
        {
            _sessions.Start();
            Core.Registrator.Register(_scoring);
            _commands.RegisterAll();
            _scoreboard.Start();
        }

        // Outside the probe gate: the reveal is a user message carrying no core data, so a schema
        // core cannot read is no reason to stop showing the ranks CS2 already holds.
        Core.Registrator.Register(_revealer);
        _revealer.Start();

        if (_loadedIntoRunningServer) OnGameThread(TakeOverConnectedPlayers);

        // Fires whether or not the probe passed — a module needs to know core settled either way.
        _api.MarkCoreReady();

        if (_loadedIntoRunningServer) PrepareForHotLoad();
        else Core.Scheduler.NextTick(ConfirmServerStart);
    }

    /// <summary>Migrates modules once every plugin has had a chance to register its assembly, runs the start's maintenance pass, then reports a server nobody scores on.</summary>
    /// <remarks>
    /// SwiftlyS2 raises it only while it starts, so a load into a running server never reaches it —
    /// <see cref="PrepareForHotLoad"/> covers that — and it is what confirms a start to <see cref="ConfirmServerStart"/>.
    /// SwiftlyS2 itself loading mid-game raises it after a Load that found a map up and scheduled the delayed scoring
    /// check, which is cancelled so the warning is not logged twice.
    /// </remarks>
    public override void OnAllPluginsLoaded()
    {
        _allPluginsLoaded = true;
        _hotLoadCheck?.Cancel();

        _migrator.MigratePendingModules();
        RunStartupMaintenance();
        WarnIfNobodyIsScoring();
    }

    public override void Unload()
    {
        _hotLoadCheck?.Cancel();
        _migrator?.Stop();
        _commands?.UnregisterAll();
        _revealer?.Dispose();
        _scoreboard?.Dispose();
        _sessions?.Stop();
        _sessions?.FlushOnUnload(UnloadFlushBudget);
        _migrator?.WaitForRunning(UnloadMigrationBudget);
        _events?.Clear();

        _commandsConfig?.Dispose();
        _ladderConfig?.Dispose();
        _points?.Dispose();
        _config?.Dispose();
    }

    private void LoadConfiguration()
    {
        _config = ConfigFiles.Main(Core, _logger);
        _points = ConfigFiles.Points(Core, _logger);
        _ladderConfig = ConfigFiles.Ladder(Core, _logger);
        _commandsConfig = ConfigFiles.Commands(Core, _logger);
        _detail = new DetailLog(_logger, _config);
    }

    private void OpenDatabase()
    {
        _tables = new TableNames(_config.Current.Database);
        _database = DatabaseConnection.Resolve(Core, _config.Current.Database.Connection, _logger);
        _store = new PlayerStore(_database, _tables);
        _migrator = new SchemaMigrator(Core, _database, _tables, _logger);
    }

    private void BuildServices()
    {
        _registry = new PlayerRegistry();
        _ladder = new RankLadder(() => _ladderConfig.Current.Tiers);
        _events = new RanksEventHub(_logger);

        _sessions = new PlayerSessions(Core, _config, _store, _registry, _ladder, _events, _detail, _logger);
        _awards = new AwardPipeline(Core, _config, _points, _ladder, _registry, _events, _detail, _logger);
        _scoreboard = new ScoreboardService(Core, _config, _ladder, _registry, _logger);
        _revealer = new RankRevealer(Core, _config, _detail, _logger);
        _scoring = new ScoringHandlers(Core, _config, _awards, _registry, _detail, _logger);
        _reset = new PlayerReset(_ladder, _sessions, _scoreboard, _events);
        _menuEntries = new MenuEntryRegistry(_logger);

        // Before the menus, which read the leaderboard through it. Nothing in the API needs them
        // back, so the order is a one-way dependency rather than a cycle to break.
        _api = new RanksApi(
            Core, _config, _points, _registry, _sessions, _awards, _ladder, _scoreboard,
            _store, _migrator, _menuEntries, _tables, _database, _events, _logger);

        _menus = new RankMenus(Core, _ladder, _registry, _commandsConfig, _reset, _menuEntries, _api, _logger);

        _commands = new CommandRegistry(Core, _commandsConfig, _menus, _registry, _reset, _api, _logger);

        // The scoreboard follows points rather than polling, so core never fights another plugin
        // for the clan-tag field.
        _events.RankChanged += e => _scoreboard.Apply(e.SteamId);
        _events.RankChanged += e => _revealer.OnRankChanged(e.SteamId);
        _events.PlayerDataLoaded += snapshot => _scoreboard.Apply(snapshot.SteamId);
        _events.PeriodChanged += _ => OnPeriodChanged();
        _scoring.RoundEnded += OnRoundEnded;
    }

    private void OnRoundEnded()
    {
        _sessions.RollOverIfDue();
        _ = _sessions.SaveAllAsync();
        _ = _sessions.RunMaintenanceAsync(force: false);
        _scoreboard.ApplyAll();
    }

    /// <summary>Redraws every scoreboard and tells the server the season turned over.</summary>
    /// <remarks>
    /// Without the announcement every rank on the server drops at once with nothing to explain it,
    /// which reads as lost data rather than a new season.
    /// </remarks>
    private void OnPeriodChanged()
    {
        _scoreboard.ApplyAll();

        foreach (var player in Core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                Chat.Send(Core, player, "period.started");
    }

    /// <summary>Adopts the players a load into a running server finds connected, and re-sends them the rank reveal.</summary>
    /// <remarks>
    /// Tracking reads each player's name and the reveal is a user message, both native, while Load may be off the game
    /// thread: SwiftlyS2's file watcher runs it on a pool thread, and a plugin-API load on the caller's. The reveal goes
    /// out whatever the schema probe said, like every other reveal.
    /// </remarks>
    private void TakeOverConnectedPlayers()
    {
        if (_sessions.SchemaUsable) _sessions.AdoptConnectedPlayers();

        _revealer.RevealToAll();
    }

    /// <summary>Runs <paramref name="work"/> now when on the game thread, else on its next tick.</summary>
    private void OnGameThread(Action work)
    {
        if (Core.IsGameThread) work();
        else Core.Scheduler.NextTick(work);
    }

    /// <summary>Does what OnAllPluginsLoaded does at server start, the way a running server allows, for a load that never reaches it.</summary>
    /// <remarks>
    /// SwiftlyS2 re-runs only the handshake then. Modules are migrated in the background as they register — any already
    /// queued first — the scoring check waits until a rating module has had the chance to claim from that handshake,
    /// and maintenance runs only on a running server's terms: when due, with nobody connected (FR-DB-09).
    /// </remarks>
    private void PrepareForHotLoad()
    {
        _migrator.MigrateOnRegister();
        _hotLoadCheck = Core.Scheduler.DelayBySeconds(HotLoadCheckDelaySeconds, WarnIfNobodyIsScoring);
        _ = _sessions.RunMaintenanceAsync(force: false);
    }

    /// <summary>On the first tick, gives a load that took itself for a server start what a running server needs, unless OnAllPluginsLoaded came first.</summary>
    /// <remarks>
    /// Every real start reaches its first tick after OnAllPluginsLoaded: SwiftlyS2 raises it inside its own start, before
    /// the engine runs a frame. Without it, the load found no map on a running server — a plugin-API load landing between
    /// maps, where nothing shows the engine keeping <c>GlobalVars</c> readable. Its players re-enter on that map load, so
    /// the migrations and the checks are all it lacks.
    /// </remarks>
    private void ConfirmServerStart()
    {
        if (!_allPluginsLoaded) PrepareForHotLoad();
    }

    /// <summary>The maintenance pass a server start forces (FR-DB-09), skipped after a Load that found a map up.</summary>
    /// <remarks>
    /// Here rather than in Load, so only a start SwiftlyS2 confirms forces it: a load that finds no map may be a
    /// plugin-API load between maps, with players connected, and that never gets here. Getting here after a Load that
    /// found a map up means SwiftlyS2 itself started on a running server.
    /// </remarks>
    private void RunStartupMaintenance()
    {
        if (!_loadedIntoRunningServer) _ = _sessions.RunMaintenanceAsync(force: true);
    }

    /// <summary>Whether this load lands on a server already running, which <paramref name="hotReload"/> alone does not say.</summary>
    /// <remarks>
    /// SwiftlyS2's plugin API — <c>IPluginManager.LoadPlugin</c> and <c>ReloadPlugin</c> — passes it false on a running
    /// server. Plugins load at server start before the first map, so a map being up is what tells the two apart.
    /// </remarks>
    private bool LoadsIntoRunningServer(bool hotReload) => hotReload || MapIsLoaded();

    /// <summary>Whether the engine has a map up; false when that cannot be read.</summary>
    /// <remarks>
    /// <c>GlobalVars</c> throws until the first map, and unguarded in Load that throw aborts the load (NFR-04), so the
    /// one read Load makes is this guarded probe.
    /// </remarks>
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

    /// <remarks>
    /// Waits for every plugin, because a rating module claims scoring from its handshake, which runs
    /// after this plugin's Load. Silent while one holds the claim: that module is scoring (FR-PTS-00).
    /// </remarks>
    private void WarnIfNobodyIsScoring()
    {
        if (_config.Current.Rank.UseCoreRankingLogic || _awards.ScoringOwner is not null) return;

        _logger.LogWarning(
            "Ranks: Rank.UseCoreRankingLogic is false and no module has claimed scoring, so core awards no points. " +
            "That is correct only with a rating module too old to claim it, such as Ranks_EloPoints before 1.2.0; " +
            "otherwise nobody is awarding anything.");
    }

    /// <summary>Runs core's own migrations, keeping the failure for the probe to judge.</summary>
    /// <remarks>
    /// Does not rethrow: a migration that fails against a reachable database leaves an install
    /// that can still read and write, which is the normal shape on a host granting DML but not
    /// DDL. The probe decides whether that is survivable (§4.6).
    /// </remarks>
    private Exception? MigrateSchema()
    {
        try
        {
            _migrator.MigrateCore();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

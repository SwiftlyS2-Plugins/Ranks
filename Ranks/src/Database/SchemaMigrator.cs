using System.Data;
using System.Data.Common;
using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.Database;

/// <summary>Runs core's migrations and every registered module's, each against its own ledger.</summary>
/// <remarks>
/// Core owns the runner because affix application is the whole point of FR-DB-03 and FluentMigrator
/// offers no table-name convention to intercept. A module registering its assembly is also the only
/// shape that works in time: a module's Load() runs before the handshake, so it has no IRanksApi to
/// ask for a table name at the moment it would need one.
/// </remarks>
internal sealed class SchemaMigrator
{
    private readonly ISwiftlyCore _core;
    private readonly DatabaseConnection _database;
    private readonly TableNames _tables;
    private readonly ILogger _logger;

    /// <remarks>
    /// Guards both sets, the switch and the background chain. A module registers from its handshake, which SwiftlyS2
    /// runs on the game thread for a console load or reload and on a pool thread for its file watcher. Never held while
    /// a background migration runs, or the next handshake would wait for it.
    /// </remarks>
    private readonly Lock _gate = new();

    /// <remarks>
    /// The handshake re-runs on every plugin load and unload, so modules re-register constantly.
    /// MigrateUp() on an up-to-date ledger is cheap but not free.
    /// </remarks>
    private readonly HashSet<(Assembly Assembly, string PluginId)> _migrated = [];

    private readonly List<(Assembly Assembly, string PluginId)> _pending = [];

    /// <summary>True once nothing will drain the queue again, so a registration is migrated in the background at once.</summary>
    private bool _migratesOnRegister;

    /// <summary>The background migration queued last; the next one runs after it.</summary>
    private Task _background = Task.CompletedTask;

    /// <summary>True once core is unloading, so a background migration that has not started yet is dropped.</summary>
    private volatile bool _stopped;

    public SchemaMigrator(ISwiftlyCore core, DatabaseConnection database, TableNames tables, ILogger logger)
    {
        _core = core;
        _database = database;
        _tables = tables;
        _logger = logger;
    }

    /// <summary>Queues a module's assembly, or past startup migrates it in the background. Idempotent, and never throws.</summary>
    /// <remarks>
    /// Runs inside the registering module's handshake, which SwiftlyS2 does not guard (FR-DB-05): an exception escaping
    /// it fails every plugin on the server.
    /// </remarks>
    public void Register(Assembly assembly, string pluginId)
    {
        var key = (assembly, pluginId);

        lock (_gate)
        {
            if (_migrated.Contains(key) || _pending.Contains(key)) return;

            if (_migratesOnRegister) QueueModule(key);
            else _pending.Add(key);
        }
    }

    /// <summary>Migrates every module in the background from here on, starting with any registration already queued.</summary>
    /// <remarks>
    /// For a core that will not reach OnAllPluginsLoaded, the only other drain: one loaded into a running server, which
    /// calls this before any module registers, or one that took itself for a server start and learns otherwise on its
    /// first tick, by when the handshake has queued every module.
    /// </remarks>
    public void MigrateOnRegister()
    {
        lock (_gate)
        {
            _migratesOnRegister = true;

            foreach (var key in _pending) QueueModule(key);

            _pending.Clear();
        }
    }

    /// <summary>Migrates core's own schema. Throws — the caller decides what a failure means.</summary>
    /// <remarks>On the loading thread, unlike a module's: core cannot serve until it knows its schema is usable.</remarks>
    public void MigrateCore()
    {
        WarnOnExportedMigrator();
        Run(typeof(SchemaMigrator).Assembly, _tables.CoreLedger, "Ranks");
    }

    /// <summary>Migrates every queued module, then every later registration in the background as it arrives.</summary>
    /// <remarks>Here rather than in the background: this is server start, before any player can connect to wait on it.</remarks>
    public void MigratePendingModules()
    {
        lock (_gate)
        {
            _migratesOnRegister = true;

            foreach (var key in _pending) MigrateModule(key);

            _pending.Clear();
        }
    }

    /// <summary>Drops every background migration that has not started; <see cref="WaitForRunning"/> waits for the one already running.</summary>
    /// <remarks>For Unload. The next load of core keeps no record of this one's runs and migrates every module again.</remarks>
    public void Stop() => _stopped = true;

    /// <summary>Waits up to <paramref name="budget"/> for the background migration already running, for Unload after <see cref="Stop"/>.</summary>
    /// <remarks>
    /// SwiftlyS2 loads every plugin into a load context of its own, so the next load of core shares no lock with this
    /// one, and would start migrating the same module while this run still works on its tables and ledger. After
    /// <see cref="Stop"/> every run behind it is a no-op, so the end of the chain is the end of that run. Logged once,
    /// ungated, when the budget runs out.
    /// </remarks>
    public void WaitForRunning(TimeSpan budget)
    {
        Task running;

        lock (_gate) running = _background;

        if (Task.WaitAny([running], budget) < 0)
            _logger.LogWarning(
                "Ranks: a module migration was still running after a further {Seconds}s of unloading; loading Ranks " +
                "again straight away may migrate that module twice at once.",
                budget.TotalSeconds);
    }

    /// <summary>Migrates one module on the calling thread. Called under the gate.</summary>
    /// <remarks>Recorded as migrated even when it fails, as a queued one is, so the handshake does not retry it on every rebuild.</remarks>
    private void MigrateModule((Assembly Assembly, string PluginId) key)
    {
        _migrated.Add(key);

        if (Locate(key) is { } path) Migrate(key, path);
    }

    /// <summary>Queues one module's migrations behind every background migration already queued. Called under the gate.</summary>
    /// <remarks>
    /// Off the registering thread: a console load or reload runs the handshake on the game thread, which would otherwise
    /// stall while the module's assembly loads and its ledger is checked. The file is found here, on the thread that
    /// registers or drains the queue, never on the background chain, because SwiftlyS2's plugin list is not safe to read
    /// while another load may be changing it.
    /// </remarks>
    private void QueueModule((Assembly Assembly, string PluginId) key)
    {
        _migrated.Add(key);

        if (Locate(key) is not { } path) return;

        _background = _background.ContinueWith(
            _ => MigrateUnlessStopped(key, path), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void MigrateUnlessStopped((Assembly Assembly, string PluginId) key, string path)
    {
        if (!_stopped) Migrate(key, path);
    }

    /// <summary>Migrates one module against its own ledger, isolating every failure. Never throws.</summary>
    /// <remarks>
    /// Core has taken on running third-party code (FR-DB-05 req. 9); an unhandled throw from one
    /// module would otherwise take core's schema and every other module down with it.
    /// </remarks>
    private void Migrate((Assembly Assembly, string PluginId) key, string path)
    {
        try
        {
            RunModule(key.Assembly, path, key.PluginId);
        }
        catch (Exception ex)
        {
            ReportModuleFailure(ex, key.PluginId);
        }
    }

    /// <summary>The module's assembly file, or null — reported — when it cannot be found.</summary>
    private string? Locate((Assembly Assembly, string PluginId) key)
    {
        try
        {
            return LocateModule(key.Assembly, key.PluginId);
        }
        catch (Exception ex)
        {
            ReportModuleFailure(ex, key.PluginId);
            return null;
        }
    }

    private void ReportModuleFailure(Exception ex, string pluginId) =>
        _logger.LogError(ex, "Ranks: migrations for module {PluginId} failed; core and other modules are unaffected.", pluginId);

    /// <summary>Migrates a second load of the module's assembly, bound to core's FluentMigrator, then unloads it.</summary>
    private void RunModule(Assembly assembly, string path, string pluginId)
    {
        var context = new ModuleMigrationContext(assembly);

        try
        {
            Run(context.LoadModule(path), _tables.LedgerFor(pluginId), pluginId);
        }
        finally
        {
            context.Unload();
        }
    }

    private void Run(Assembly assembly, string ledger, string pluginId)
    {
        AdoptLegacyLedger(ledger);

        var before = ListTables();

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                ConfigureEngine(rb);
                rb.WithGlobalConnectionString(MigratorConnectionString())
                  .WithVersionTable(new AffixedVersionTable(ledger))
                  .ScanIn(assembly).For.Migrations();
            })
            .AddLogging(lb => lb.AddFluentMigratorConsole());

        DecorateProcessor(services);

        var provider = services.BuildServiceProvider(false);

        try
        {
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        finally
        {
            // Leaving it alive leaks the processor and its connection; on SQLite it holds the
            // database file open across hot reload.
            provider.Dispose();
        }

        VerifyAffixes(before, pluginId);
    }

    /// <summary>Moves a ledger off the name builds before 1.2.0 gave it by affixing it twice.</summary>
    /// <remarks>
    /// Before the runner reads it: under the old name a prefixed install sees an empty ledger and
    /// runs every migration again. Throws when the tables cannot be read, rather than assume there
    /// is nothing to move.
    /// </remarks>
    private void AdoptLegacyLedger(string ledger)
    {
        var legacy = _tables.LegacyLedger(ledger);
        var current = _tables.Resolve(ledger);
        if (string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase)) return;

        using var connection = _database.Create();
        connection.Open();

        switch (DatabaseTables.RenameIfOnlyOld(connection, Driver, legacy, current))
        {
            case TableRename.Renamed:
                _logger.LogInformation("Ranks: renamed migration ledger {Legacy} to {Current}.", legacy, current);
                break;

            case TableRename.BothExist:
                _logger.LogWarning(
                    "Ranks: migration ledgers {Legacy} and {Current} both exist; the second is used. " +
                    "Drop the first once you no longer need it.",
                    legacy, current);
                break;
        }
    }

    /// <summary>The module's assembly file, which <see cref="ModuleMigrationContext"/> loads a second time.</summary>
    /// <remarks>
    /// SwiftlyS2 loads plugins from memory, so Location is empty. Its plugin manager matches a
    /// metadata id or a folder name, and a module's folder is named after its assembly.
    /// </remarks>
    private string LocateModule(Assembly assembly, string pluginId)
    {
        if (!string.IsNullOrEmpty(assembly.Location)) return assembly.Location;

        var name = assembly.GetName().Name!;

        foreach (var id in new[] { pluginId, name })
        {
            if (_core.PluginManager.GetPluginPath(id) is not { } directory) continue;

            var path = Path.Combine(directory, $"{name}.dll");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Ranks: cannot find {name}.dll for module {pluginId}.", $"{name}.dll");
    }

    /// <remarks>
    /// Older releases exported FluentMigrator, and unzipping an update over one leaves the files
    /// behind. SwiftlyS2 forces every export onto every plugin, so they keep breaking other plugins'
    /// migrations while Ranks itself runs fine.
    /// </remarks>
    private void WarnOnExportedMigrator()
    {
        string[] stale;

        try { stale = Directory.GetFiles(Path.Combine(_core.PluginPath, "resources", "exports"), "FluentMigrator*.dll"); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (stale.Length == 0) return;

        _logger.LogError(
            "Ranks: delete {Files} from Ranks/resources/exports and restart the server. SwiftlyS2 " +
            "forces exported assemblies onto every plugin, so these replace the FluentMigrator other " +
            "plugins ship and break their migrations (VIPCore: \"Method 'get_Quoter' ... does not have " +
            "an implementation\").",
            string.Join(", ", stale.Select(Path.GetFileName)));
    }

    /// <summary>The named connection's string, with SwiftlyS2's MySQL timeout key corrected.</summary>
    /// <remarks>
    /// <c>DatabaseConnectionInfo.ToString()</c> writes Npgsql's <c>Timeout=</c> for mysql too, and
    /// MySqlConnector throws "Option 'timeout' not supported" on it. SwiftlyS2's own factory sets
    /// <c>ConnectionTimeout</c> instead, so only code handed the raw string breaks, and only when an
    /// operator configures a non-zero timeout. Postgres is correct as-is. Parsed rather than
    /// string-replaced: a password may contain ';' or '=' and arrives quoted.
    /// </remarks>
    private string MigratorConnectionString()
    {
        var raw = _core.Database.GetConnectionString(_database.Name);
        var driver = _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;

        if (driver is not ("mysql" or "mariadb")) return raw;

        var builder = new DbConnectionStringBuilder { ConnectionString = raw };

        if (!builder.TryGetValue("timeout", out var timeout)) return raw;

        builder.Remove("timeout");

        if (!builder.ContainsKey("connectiontimeout")) builder["ConnectionTimeout"] = timeout;

        return builder.ConnectionString;
    }

    /// <remarks>
    /// Dispatch is on the Driver string, never on the CLR type of the connection: the framework
    /// resolves a connection by name and the concrete type tells you which ADO provider is in play,
    /// not which SQL dialect the operator configured.
    /// </remarks>
    private void ConfigureEngine(IMigrationRunnerBuilder rb)
    {
        var driver = _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;

        switch (driver)
        {
            case "mysql" or "mariadb":
                rb.AddMySql5();
                break;

            case "postgresql" or "postgres" or "pgsql":
                rb.AddPostgres();
                break;

            case "sqlite":
                rb.AddSQLite();
                break;

            default:
                throw new InvalidOperationException(
                    $"Ranks: database driver '{_database.Info.Driver}' is not supported. " +
                    "Supported drivers are mysql, postgresql and sqlite (FR-DB-01).");
        }
    }

    /// <remarks>
    /// Replaces whichever accessor the engine registered rather than re-registering
    /// IMigrationProcessor: the runner reads IProcessorAccessor.Processor, so anything else would
    /// decorate a service nothing resolves.
    /// </remarks>
    private void DecorateProcessor(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IProcessorAccessor))
            ?? throw new InvalidOperationException(
                "Ranks: FluentMigrator registered no IProcessorAccessor, so table affixes cannot be enforced.");

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProcessorAccessor),
            sp => new AffixingProcessorAccessor(ResolveOriginal(sp, descriptor), _tables),
            descriptor.Lifetime));
    }

    private static IProcessorAccessor ResolveOriginal(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProcessorAccessor instance) return instance;
        if (descriptor.ImplementationFactory is not null) return (IProcessorAccessor)descriptor.ImplementationFactory(sp);

        return (IProcessorAccessor)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }

    /// <summary>Reports any table a migration created without the configured affixes.</summary>
    /// <remarks>
    /// Execute.Sql carries no expression tree, so the decorator cannot reach inside it — enforcement
    /// for the expression API, detection for the rest (FR-DB-05 req. 10).
    /// </remarks>
    private void VerifyAffixes(IReadOnlySet<string> before, string pluginId)
    {
        if (!_tables.HasAffixes) return;

        try
        {
            foreach (var table in ListTables())
            {
                if (before.Contains(table) || _tables.CarriesAffixes(table)) continue;

                _logger.LogError(
                    "Ranks: module {PluginId} created table '{Table}' without the configured prefix/suffix. " +
                    "Raw Execute.Sql DDL cannot be rewritten — use the expression API so core can apply affixes.",
                    pluginId, table);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not verify table affixes for module {PluginId}.", pluginId);
        }
    }

    private HashSet<string> ListTables()
    {
        try
        {
            using var connection = _database.Create();
            connection.Open();

            return DatabaseTables.List(connection, Driver);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not list existing tables; affix verification is skipped this run.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private string Driver => _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;
}

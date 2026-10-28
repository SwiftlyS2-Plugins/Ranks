using System.Data;
using System.Reflection;
using FluentMigrator;
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
    /// The handshake re-runs on every plugin load and unload, so modules re-register constantly.
    /// MigrateUp() on an up-to-date ledger is cheap but not free.
    /// </remarks>
    private readonly HashSet<(Assembly Assembly, string PluginId)> _migrated = [];

    private readonly List<(Assembly Assembly, string PluginId)> _pending = [];

    public SchemaMigrator(ISwiftlyCore core, DatabaseConnection database, TableNames tables, ILogger logger)
    {
        _core = core;
        _database = database;
        _tables = tables;
        _logger = logger;
    }

    /// <summary>Queues a module's assembly. Idempotent — safe to call from every handshake.</summary>
    public void Register(Assembly assembly, string pluginId)
    {
        var key = (assembly, pluginId);

        if (_migrated.Contains(key) || _pending.Contains(key)) return;

        _pending.Add(key);
    }

    /// <summary>Migrates core's own schema. Throws — the caller decides what a failure means.</summary>
    public void MigrateCore() =>
        Run(typeof(SchemaMigrator).Assembly, _tables.CoreLedger, "Ranks");

    /// <summary>Migrates every queued module, isolating each failure.</summary>
    /// <remarks>
    /// Core has taken on running third-party code (FR-DB-05 req. 9); an unhandled throw from one
    /// module would otherwise take core's schema and every other module down with it.
    /// </remarks>
    public void MigratePendingModules()
    {
        if (_pending.Count == 0) return;

        foreach (var (assembly, pluginId) in _pending.ToArray())
        {
            try
            {
                Run(assembly, _tables.LedgerFor(pluginId), pluginId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: migrations for module {PluginId} failed; core and other modules are unaffected.", pluginId);
            }
            finally
            {
                _migrated.Add((assembly, pluginId));
            }
        }

        _pending.Clear();
    }

    private void Run(Assembly assembly, string ledger, string pluginId)
    {
        WarnOnSplitMigratorIdentity(assembly, pluginId);

        var before = ListTables();

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                ConfigureEngine(rb);
                rb.WithGlobalConnectionString(_core.Database.GetConnectionString(_database.Name))
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

    /// <remarks>
    /// ScanIn().For.Migrations() matches on core's IMigration, so a module that resolved its own
    /// FluentMigrator contributes zero migrations and MigrateUp() reports success over a schema it
    /// never created. Attributes are compared by name because the whole failure is that the two
    /// Types are not equal. Fix is packaging, not code: the module must reference FluentMigrator
    /// with ExcludeAssets="runtime" and take core's exported copy.
    /// </remarks>
    private void WarnOnSplitMigratorIdentity(Assembly assembly, string pluginId)
    {
        Type[] types;

        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.OfType<Type>()]; }

        if (types.Any(t => typeof(IMigration).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })) return;

        var declared = types.Count(t => t.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "FluentMigrator.MigrationAttribute"));

        if (declared == 0) return;

        _logger.LogError(
            "Ranks: module {PluginId} declares {Count} migration(s) that core cannot see. Its " +
            "FluentMigrator is a second copy, so no table will be created. Ship the module with " +
            "FluentMigrator ExcludeAssets=\"runtime\" and let it bind to Ranks/resources/exports.",
            pluginId, declared);
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
        var sql = _database.Info.Driver?.Trim().ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()",
            "postgresql" or "postgres" or "pgsql" => "SELECT tablename FROM pg_tables WHERE schemaname = current_schema()",
            "sqlite" => "SELECT name FROM sqlite_master WHERE type = 'table'",
            _ => null,
        };

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (sql is null) return names;

        try
        {
            using var connection = _database.Create();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0))
                    names.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not list existing tables; affix verification is skipped this run.");
        }

        return names;
    }
}

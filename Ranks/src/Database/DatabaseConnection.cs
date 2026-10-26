using System.Data;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Database;

namespace Ranks.Database;

/// <summary>The connection core and every module share, resolved and proved usable at boot.</summary>
/// <remarks>
/// Core never holds credentials: <c>Database.Connection</c> names an entry in SwiftlyS2's global
/// <c>configs/database.jsonc</c>.
/// </remarks>
internal sealed class DatabaseConnection
{
    private readonly ISwiftlyCore _core;

    private DatabaseConnection(ISwiftlyCore core, string name, DatabaseConnectionInfo info)
    {
        _core = core;
        Name = name;
        Info = info;
    }

    public string Name { get; }

    /// <remarks>
    /// Resolved once and passed down — the migration runner's engine dispatch reads
    /// <see cref="DatabaseConnectionInfo.Driver"/> off this same struct rather than querying again.
    /// </remarks>
    public DatabaseConnectionInfo Info { get; }

    /// <summary>Resolves the named connection, reports it, and throws if it cannot be opened.</summary>
    /// <remarks>
    /// Core does not police <em>which</em> database the operator pointed it at. The framework
    /// substitutes the default connection for a name it cannot find, and that substitution cannot
    /// be told apart from an entry legitimately matching the default — so the resolved target is
    /// logged and the judgement left with the operator, who owns the file (FR-DB-02).
    /// </remarks>
    public static DatabaseConnection Resolve(ISwiftlyCore core, string name, ILogger logger)
    {
        var info = core.Database.GetConnectionInfo(name);

        logger.LogInformation(
            "Ranks: database '{Name}' resolved to {Driver} {Host}:{Port}/{Database}",
            name, info.Driver, info.Host, info.Port, info.Database);

        ProveUsable(core, name);

        return new DatabaseConnection(core, name, info);
    }

    /// <summary>Hands out a fresh, <em>unopened</em> connection.</summary>
    /// <remarks>
    /// The framework returns it closed and the migration runner requires it that way:
    /// MySqlConnector strips the password from <c>ConnectionString</c> once opened, and
    /// FluentMigrator builds its own connection from that string.
    /// </remarks>
    public IDbConnection Create() => _core.Database.GetConnection(Name);

    private static void ProveUsable(ISwiftlyCore core, string name)
    {
        try
        {
            using var probe = core.Database.GetConnection(name);
            probe.Open();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Database.Connection '{name}' could not be opened. Check the entry in SwiftlyS2's " +
                "configs/database.jsonc — host, port, credentials and database name.", ex);
        }
    }
}

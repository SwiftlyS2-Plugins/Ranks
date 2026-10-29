# Ranks — Plugin Development Guidelines

> Copilot instructions for **Ranks**, the official SwiftlyS2 point-based
> progression and statistics plugin for Counter-Strike 2.

## Scope

This file governs **this repository only**: the `Ranks` core plugin, the
`Ranks.Contract` assembly, and the official module plugins that consume it
(WeaponStats, EloPoints, CustomCombos, Prestige).

The functional specification lives in [`docs/functional-requirements.md`](../docs/functional-requirements.md).
**That document is the authority on *what* to build; this one covers *how* to
build it on SwiftlyS2.** When the two disagree about behaviour, the
requirements doc wins — fix this file rather than diverging.

Framework rules below are general SwiftlyS2 facts. Anything specific to one
module belongs in that module's own `.github/copilot-instructions.md`, and must
never restate the framework rules here — a second copy goes stale and then
silently overrides the real guide.

## Code comments

**A method body contains no comments.** Everything a reader needs comes from the
method name, the structure of the code, and the XML doc above it. If a body
needs a comment to be understood, the body is wrong — not under-commented.

### Make the code carry it

| Instead of | Do this |
|---|---|
| A comment labelling a block | Extract that block into a named method |
| A comment explaining what a value holds | Name the value |
| A comment explaining a condition | Name the condition — `if (IsEligibleForAward(e))` |
| Nested `if`s with a comment per branch | Guard clauses, early return |
| A comment marking sections of a long method | Split the method |

The test is mechanical: **if you are about to write a comment inside a method,
extract instead.** The comment you wanted becomes the extracted method's name or
its `<summary>`, which is both closer to the thing and visible at every call
site.

### The doc comment above it

```csharp
/// <summary>
/// Awards <paramref name="eventKey"/> to the player, applying gates and core's
/// multipliers. No-op when core ranking logic is disabled.
/// </summary>
/// <remarks>
/// Must run on the game thread — reads <c>CCSPlayerController</c> to resolve the team.
/// </remarks>
private void Award(ulong steamId, string eventKey, int count = 1)
```

- `<summary>` — 1–2 lines. What it does, and why it exists if that is not
  obvious. Never a paragraph; a doc comment that needs paragraphs is a design
  note filed in the wrong place.
- `<param>` / `<returns>` — only when the name does not already say it.
  `<param name="count">The count.</param>` is noise.
- `<remarks>` — **the one escape hatch**, below.

### The one escape hatch

Some facts cannot be expressed by renaming anything, because they are not in the
code at all:

- framework behaviour (`Handled` is a no-op in a post hook; `GetConnection`
  falls back to the default on an unknown name)
- engine behaviour (a networked write needs its `*Updated()` notifier)
- cross-plugin constraints (Dommel's resolver is process-wide)
- a chosen value (why *this* interval, cap or bound)
- a deliberate omission (why something expected is **absent** — the highest-value
  kind, because absence cannot be read)

These go in **`<remarks>` above the method, never inline.** One or two lines,
stating the fact and its consequence.

If such a fact genuinely belongs to one statement buried in a method, that is
the signal to extract that statement into its own method so the fact can live in
*its* summary. The rule holds all the way down.

### Never

- A comment restating the next line.
- Narrating the change — `// changed this to fix X`. That is a commit message.
- Re-deriving rationale that already lives in `functional-requirements.md` or
  `implementation-guide.md`. Cite the requirement id instead: `<remarks>FR-DB-06.</remarks>`
- Commented-out code.
- `TODO` without a tracking issue.

> **The code samples in `docs/` are deliberately annotated and do not follow
> this rule.** They are teaching material, where the inline comment *is* the
> point being made. Production code under `src/` follows the rule above.

---

## Table of Contents

1. [Plugin Structure & Base Variables](#1-plugin-structure--base-variables)
2. [Configuration](#2-configuration)
3. [Database](#3-database)
4. [Logging](#4-logging)
5. [Commands](#5-commands)
6. [Translations / Localization](#6-translations--localization)
7. [Events, Listeners & Hooks](#7-events-listeners--hooks)
8. [Shared Interfaces](#8-shared-interfaces)
9. [Thread Safety & Async Data Access](#9-thread-safety--async-data-access)
10. [Data Management & Optimization](#10-data-management--optimization)
11. [Menus & UI](#11-menus--ui)
12. [Player Validation](#12-player-validation)
13. [Plugin Lifecycle](#13-plugin-lifecycle)
14. [Auditing & Bug Prevention](#14-auditing--bug-prevention)
15. [SwiftlyS2 Docs & CS2 Schema Dumps via @sd MCP](#15-swiftlys2-docs--cs2-schema-dumps-via-sd-mcp)
16. [Build & Release](#16-build--release)

---

## 1. Plugin Structure & Base Variables

### Plugin Class Declaration

```csharp
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

[PluginMetadata(
    Id = "Ranks",
    Version = "1.0.0",
    Name = "Ranks",
    Author = "Shmitzas",
    Description = "Point-based progression and statistics system."
)]
public partial class Ranks : BasePlugin
{
    // Static so partial classes and services can reach it without threading it through.
    public static new ISwiftlyCore Core { get; private set; } = null!;

    private ILogger<Ranks> logger = null!;
    private IOptionsMonitor<Config> cfg = null!;

    public Ranks(ISwiftlyCore core) : base(core) { }
}
```

### ⚠️ Keep the Constructor Empty

`base.Core` is **not** safely usable inside the `BasePlugin` constructor.
SwiftlyS2's `PluginManager` activates the type via
`Activator.CreateInstance(pluginType, core)` **before** wiring the DI, logger
and config pipeline. Touching `Core.Configuration`, `Core.Scheduler`,
`Core.PluginDataDirectory`, `Core.PlayerManager` or `Core.Database` in the ctor
NREs with:

```
TargetInvocationException -> NullReferenceException
  at Ranks.LoadConfiguration() in Ranks.cs:LINE
  at Ranks..ctor(ISwiftlyCore core)
  at PluginManager.InstantiatePlugin(...)
```

The log only says `Failed to load plugin: (swRoot)/plugins/Ranks/Ranks.dll`, so
the cause is not obvious from the symptom.

```csharp
public Ranks(ISwiftlyCore core) : base(core)
{
    // Empty. Nothing here can safely touch Core.
}

public override void Load(bool hotReload)
{
    Core = base.Core;
    LoadConfiguration();     // config files, DI container
    InitializeDatabase();    // schema creation / migration
    RegisterCoreEvents();    // populate the event registry (see §8)
    RegisterListeners();
    RegisterCommands();
}
```

**Rule of thumb:** if it touches `Core.*`, DI or config, it belongs in
`Load(bool hotReload)`.

### `Core.Engine.GlobalVars` throws during `Load()`

`EngineService.GlobalVars` is `ref CGlobalVars` and throws
`InvalidOperationException("GlobalVars is null.")` when the engine is not yet
up. `Core.Engine` itself is already non-null, so an `if (Core.Engine is not { }
engine)` guard does **not** protect you. The throw is fatal — it aborts plugin
loading.

Any `GlobalVars` read reachable from `Load()` must be inside `NextTick` / a
scheduler callback / an event handler, and wrapped in try/catch if the first
tick can still be too early.

### Key `Core` Sub-Services

| Service | Purpose |
|---------|---------|
| `Core.PlayerManager` | `GetAllPlayers()`, `GetAllValidPlayers()`, `SendChat()` |
| `Core.Command` | `RegisterCommand()`, `RegisterCommandAlias()`, `UnregisterCommand()` |
| `Core.GameEvent` | `HookPre<T>()`, `HookPost<T>()`, `FireToPlayerAsync<T>()` |
| `Core.Event` | Framework lifecycle: `OnClientPutInServer`, `OnMapLoad`, `OnTick`, … |
| `Core.Scheduler` | `NextTick()`, `NextWorldUpdate()`, `DelayAndRepeatBySeconds()` |
| `Core.Configuration` | `InitializeJsonWithModel<T>()`, `GetConfigPath()` |
| `Core.Database` | `GetConnection(name)`, `GetConnectionInfo(name)` |
| `Core.Localizer` | Translations — see §6 |
| `Core.Menu` | `CreateMenu(id)` — see §11. (`Core.MenusAPI` is the `[Obsolete]` classic API) |
| `Core.Permission` | `PlayerHasPermissions(steamId, perms)` |
| `Core.Registrator` | `Register(instance)` — attribute discovery for non-plugin classes |
| `Core.CSGODirectory` | Path to the `csgo/` directory |

### Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SwiftlyS2.CS2" Version="1.4.13"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />

    <!-- Migrations ship with the plugin; the host does not provide them. -->
    <PackageReference Include="FluentMigrator" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.MySql" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.Postgres" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.SQLite" Version="8.*" />
  </ItemGroup>
</Project>
```

> `SwiftlyS2.CS2` and every `Microsoft.Extensions.*` package must carry
> `ExcludeAssets="runtime" PrivateAssets="all"` — the host already provides them,
> and shipping a second copy produces duplicate type identities. The
> FluentMigrator packages are the opposite case: nothing provides them, so they
> must ship normally.

---

## 2. Configuration

### Config Model

```csharp
namespace Ranks;

public class Config
{
    public DatabaseSettings   Database   { get; set; } = new();
    public RankSettings       Rank       { get; set; } = new();
    public ScoreboardSettings Scoreboard { get; set; } = new();
    public PointSettings      Points     { get; set; } = new();
    public VipSettings        Vip        { get; set; } = new();
    public GeneralSettings    General    { get; set; } = new();
}

public class GeneralSettings
{
    public bool DetailedLogging { get; set; } = false;   // gates Information + Debug, §4
    // …
}
```

Every setting needs a sensible default — the file is generated from the model,
so the defaults *are* the shipped config.

### Loading Configuration (canonical pattern)

**Bind `IOptionsMonitor<T>` and read through `.CurrentValue`. Never store
`IOptions<T>.Value`.** A `.Value` snapshot is frozen at `Load`, so the plugin
keeps serving stale settings after every reload — and it is silent when wrong.

```csharp
private void LoadConfiguration()
{
    const string fileName = "config.jsonc";
    const string section = "Ranks";

    Core.Configuration
        .InitializeJsonWithModel<Config>(fileName, section)   // creates only if absent
        .Configure(builder => builder.AddJsonFile(
            Core.Configuration.GetConfigPath(fileName),
            optional: false,
            reloadOnChange: true));

    ServiceCollection services = new();
    services.AddSwiftly(Core, addLogger: true, addConfiguration: true)
        .AddOptionsWithValidateOnStart<Config>()
        .BindConfiguration(section);

    // A local BuildServiceProvider() is fine here — this is not the ASP.NET
    // "don't build a second container" case.
    var provider = services.BuildServiceProvider();
    logger = provider.GetRequiredService<ILogger<Ranks>>();
    cfg = provider.GetRequiredService<IOptionsMonitor<Config>>();

    cfg.OnChange(_ => logger.LogWarning("Ranks: {File} reloaded.", fileName));
}
```

Reload notices are `LogWarning` and are **never** gated behind
`DetailedLogging` — an operator editing a live config needs the confirmation.

### Does your config actually reload? Three propagation idioms

Binding the monitor is only half of it. Whether a *read site* sees the new value
depends on how it holds the config:

| Idiom | Reloads? |
|---|---|
| Monitor read per access — `cfg.CurrentValue.Rank.MinPlayers` | yes |
| Lazy delegate — `Func<Config> get = () => cfg.CurrentValue;`, called per read | yes |
| **Swapped snapshot** — `private volatile Config _snapshot;` reassigned from `OnChange` | **yes, and it is the fastest** |
| **Constructor snapshot** — `private readonly Config _cfg;`, or extracted primitives like `_minPlayers = cfg.MinPlayers;` | **no — frozen at Load** |

The last one hides well: the plugin reloads, the log line prints, and the
service keeps using the old value.

**Rows three and four look alike and are opposites.** A field captured once and
never refreshed is frozen; a field **reassigned by `OnChange`** is current *and*
avoids a `ConcurrentDictionary` lookup per read, which matters on the award
path. Prefer it there, and read the reference once at the top of an operation
so a mid-award reload cannot mix old and new values. Implementation guide §3.2
has the full rule. `IOptions<T>.Value` is the one that is always wrong — no
change notification at all.

**Two things never reload, by design** — say so in the reload log rather than
pretending otherwise:

- `Database.Connection`, `Database.TablePrefix` and `Database.TableSuffix`.
  Changing them mid-session would point a live writer at a different table set.
- Event hooks and command bindings decided once at `Load` from a config toggle.

### Never write the watched file from a reload callback

`OnChange` fires on file change. Writing the file or calling
`IConfigurationRoot.Reload()` from inside the callback re-raises the token and
loops — and a re-entrancy flag does **not** suppress it. Seed, migrate and
sanitize on explicit load paths only.

This is one reason **core writes no config file at all** — every file under
`configs/` is hand-authored and only ever read. See the requirements doc,
§13 Configuration summary.

### ⚠️ One save notifies every bound config

All config files go into one `IConfigurationBuilder`, so they share one
`IConfigurationRoot` and one reload token. `BindConfiguration("Section")` binds
to that root, not to the file the section came from — so saving **any** watched
file raises `OnChange` on **every** `IOptionsMonitor<T>`.

NFR-08 requires the opposite: saving `points.jsonc` must re-apply only
`points.jsonc`. And one save fires `OnChange` two or three times anyway, because
editors truncate then write.

Both are fixed by comparing before applying — fingerprint the **bound model**
(not the file bytes, so reformats and comment edits are correctly ignored),
no-op when unchanged, and put every side effect after the guard. Full shape in
the implementation guide, §3.1.

### Multiple Config Files

`Ranks` splits config across `config.jsonc`, `points.jsonc`, `ranks.jsonc` and
`commands.jsonc`. Use one reusable builder that returns the monitor:

```csharp
private IOptionsMonitor<T> BuildConfigService<T>(string fileName, string sectionName)
    where T : class, new()
{
    Core.Configuration
        .InitializeJsonWithModel<T>(fileName, sectionName)
        .Configure(c => c.AddJsonFile(
            Core.Configuration.GetConfigPath(fileName),
            optional: false,
            reloadOnChange: true));

    ServiceCollection services = new();
    services.AddSwiftly(Core).AddOptions<T>().BindConfiguration(sectionName);
    return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<T>>();
}
```

### Other initializers

```csharp
Core.Configuration.InitializeWithTemplate("config.jsonc", "config.template.jsonc"); // packaged templates/
Core.Configuration.InitializeTomlWithModel<SomeConfig>("file.toml", "Section");
```

`Core.Configuration.BasePath` and `.BasePathExists` expose the config directory.
All setup methods return the service, so they chain.

---

## 3. Database

`Ranks` owns its schema and talks to the database directly. Three engines must
work: **MySQL/MariaDB, PostgreSQL and SQLite**. See the requirements doc §5 for
the schema itself.

### Connections

Named connections are defined in SwiftlyS2's global `configs/database.jsonc`;
the plugin only stores the *name*. It never holds credentials.

```jsonc
{
    "default_connection": "host",
    "connections": {
        "host":  "mysql://user:pass@localhost:3306/database",
        "stats": "postgresql://user:pass@localhost:5432/database",
        "local": "sqlite://data/local.db"
    }
}
```

```csharp
using var connection = Core.Database.GetConnection(cfg.CurrentValue.Database.Connection);
connection.Open();
```

An unknown name silently falls back to the default connection — validate the
name at load and log loudly if it is missing, or a typo quietly writes to the
wrong database.

### ⚠️ Dispatch on `Driver`, never on the connection's CLR type

```csharp
// WRONG — the SQLite branch is dead code.
switch (connection)
{
    case MySqlConnection:  rb.AddMySql5();   break;
    case NpgsqlConnection: rb.AddPostgres(); break;
    case SqliteConnection: rb.AddSQLite();   break;   // never matches
    default: throw new NotSupportedException();
}

// RIGHT
var driver = Core.Database.GetConnectionInfo(name).Driver;   // "mysql" | "postgresql" | "sqlite"
switch (driver)
{
    case "mysql":      rb.AddMySql5();   break;
    case "postgresql": rb.AddPostgres(); break;
    case "sqlite":     rb.AddSQLite();   break;
    default: throw new NotSupportedException($"Unsupported driver: {driver}");
}
```

SwiftlyS2 builds SQLite connections from **`System.Data.SQLite`**, not
`Microsoft.Data.Sqlite`. A `case SqliteConnection:` is CLR *type identity*, so it
never matches, falls through to `default:` and throws — breaking every fresh
SQLite install while MySQL and PostgreSQL look fine.

### Portability rules

These are the ones that bite:

- **No identifier named `rank`.** Reserved in MySQL 8.
- **Upserts must be a single statement against a real PK/unique constraint.**
  `ON DUPLICATE KEY UPDATE col = VALUES(col)` on MySQL/MariaDB (MariaDB has no
  `AS new`), `ON CONFLICT (pk) DO UPDATE SET col = excluded.col` on
  PostgreSQL/SQLite. "UPDATE, then INSERT on zero rows, catch duplicate-key" is
  **not** portable — a failed statement aborts the surrounding PostgreSQL
  transaction.
- **A missing unique constraint fails differently per engine.** MySQL's
  `ON DUPLICATE KEY UPDATE` silently inserts duplicates forever; PostgreSQL and
  SQLite throw. Every upserted table needs its natural key declared.
- **SQLite cannot `ALTER COLUMN`** and rejects `ADD CONSTRAINT … PRIMARY KEY`.
  Declare multi-column primary keys inline on `Create.Table`, which works on all
  three.
- **Migration runners abort at the first failure**, so one engine-specific
  migration failing hides every migration after it.
- **Verify each engine end-to-end.** A migration that no-ops because of a
  mismatched database-identifier string records as applied having done nothing,
  with no error. Fresh *and* existing databases, all three engines.

### Schema versioning — FluentMigrator

Full rules in the requirements doc, FR-DB-05. The five that cause silent damage:

1. **The version ledger must carry the configured prefix/suffix.** Otherwise the
   first server to boot records every migration, and the second server — with a
   different prefix — sees them as applied, creates nothing, and fails every
   query. Supply it via `IVersionTableMetaData`; `DefaultVersionTableMetaData`
   cannot be cheaply subclassed in 8.x.
2. **`IfDatabase("Postgres")` matches nothing** — no error, the block is skipped
   and the migration records as applied. The working aliases are `MySql5` /
   `MariaDB` / `MySql`, **`PostgreSQL`**, and `SQLite`. Never match on
   `DatabaseType`; it is version-stamped and moves with releases.
3. **Guard each object individually, not the migration as a whole.** One
   `if (Schema.Table(x).Exists()) return;` covering a table *and* its indexes
   *and* a second table skips the rest while still recording the migration as
   applied — permanently, since `MigrateUp()` never retries.
4. **Except `Create.PrimaryKey`**, which must not be guarded that way: MySQL
   names every PK `PRIMARY`, so the existence check is always false and the
   `ADD CONSTRAINT` then fails. Declare composite keys inline on `Create.Table`.
5. **Pass the runner an unopened connection and dispose its `ServiceProvider`.**
   MySqlConnector strips the password from `ConnectionString` on open; a leaked
   provider holds the SQLite file open across hot-reload.

A migration failure must not disable a working database — shared hosting often
grants DML but not DDL. Run migrations, keep the exception, then probe
`SELECT 1 FROM {table} WHERE 1 = 0` unconditionally and decide from the probe.

Table names go through the affix resolver (requirements doc FR-DB-03) —
including index, constraint and version-table names, and including tables owned
by modules, which obtain names via `IRanksApi.ResolveTableName`. Index names are
per-table on MySQL but schema-scoped on PostgreSQL and database-scoped on
SQLite, so an unaffixed index name works on one engine and collides on the other
two. Anything that builds a name by string concatenation will drift.

### One connection, one ledger per plugin — one runner, owned by core

Core and every module share the connection from `Database.Connection`, and each
plugin gets its own ledger table. **Core runs every migration**: a module calls
`RegisterMigrations(assembly, pluginId)` from the shared-interface handshake and
core builds the runner, derives the ledger name and rewrites table names with
the configured affixes.

That ownership is the enforcement point. FluentMigrator has no table-name
convention, so while a module owned its own runner every affix helper core could
offer was opt-in — a convention, not a guarantee. It also fixes a timing
contradiction: a module's `Load()` runs before the handshake, so it never had an
`IRanksApi` to ask for a name at the moment it needed one. Core migrates its own
schema in `Load()` and registered module assemblies in `OnAllPluginsLoaded()`,
each inside its own try/catch so one bad module cannot take the schema down.

The official `SwiftlyS2-Plugins/Admins` shares one stock `VersionInfo` across
three modules and gets away with it only by hand-coordinating migration IDs into
one sequence across three folders. A colliding third-party ID is then recorded
as applied and its table is **never created**, silently. See FR-DB-05.

Core avoids that: `TableNames.LedgerFor(pluginId)` gives every module its own
`AffixedVersionTable`, so **migration IDs only need to be unique within a module**
and two modules may share one. `Ranks_EloPoints` and `Ranks_CustomCombos` both use
`202610261200` today, which is fine and is not something to "fix".

### ⚠️ Dommel's table-name resolver is process-wide

`[Table("…")]` is compile-time, so prefix support means
`DommelMapper.SetTableNameResolver(…)` — which is **static, per-process, and
shared with every other plugin using Dommel in that host**. `Admins` does, and
will be installed alongside this one.

A resolver that prefixes unconditionally renames `bans` to `awp_bans` and breaks
that plugin with no error on either side. Delegate to
`DefaultTableNameResolver` and prefix **only types from this assembly**:

```csharp
public string ResolveTableName(Type type) =>
    type.Assembly == typeof(PlayerRecord).Assembly
        ? TableNames.Resolve(_inner.ResolveTableName(type))
        : _inner.ResolveTableName(type);
```

Install it before the first query — `Resolvers.TypeTableNameCache` caches per
type on first use, so a late install leaves already-touched entities unprefixed.

### Access patterns

- All queries run **off the game thread** and marshal results back via the
  scheduler (§9).
- Writes are **absolute** and batched by a single writer; a record is marked
  saved only after the write commits.
- Each player is isolated behind a savepoint, so one bad row skips that player
  rather than aborting the whole batch.
- `using var connection = …` always. Dispose any migration-runner service
  provider too — a leaked processor holds the SQLite file open across hot
  reload.

---

## 4. Logging

### DetailedLogging

All verbose logging is gated behind `General.DetailedLogging` in
`config.jsonc`, default **`false`**:

| Level | Gated by `DetailedLogging`? | When |
|-------|---------------------------|------|
| `LogInformation` | **yes** | Flow tracing, data values |
| `LogDebug` | **yes** | Detailed internal state |
| `LogWarning` | **no** | Unexpected but recoverable |
| `LogError` | **no** | Failures, exceptions |
| thrown exceptions | **no** | Never swallowed to honour the flag |

**Read the flag from `CurrentValue` at the call site.** It lives in a
hot-reloaded file, so a cached copy pins the value at construction and the
symptom is "I turned logging on and nothing happened".

```csharp
// Gated — the check short-circuits before any string work
if (cfg.CurrentValue.General.DetailedLogging)
    logger.LogInformation("Awarding {Points} for {EventKey} to {SteamId}", points, eventKey, steamId);

// Never gated, and always pass the exception object
logger.LogError(ex, "Failed to load record for {SteamId}", steamId);
logger.LogWarning("Core ranking logic is disabled; no module is writing points.");

// Structured placeholders, not interpolation
logger.LogInformation($"Awarding {points}");   // BAD — builds the string regardless
```

`DetailedLogging` decides whether *successful, expected* flow is narrated. It
never downgrades or silences a failure.

### Level choices specific to this plugin

- **`UseCoreRankingLogic = false`** logs one ungated `Warning` at
  `OnAllPluginsLoaded`. Core has no registry and therefore cannot tell whether a
  rating module is present, so the warning is unconditional and says so.
- A **scoring decision** (gate rejected, multiplier applied) is `Debug`.
- A **recoverable persistence failure** is `Warning`; anything that risks data
  loss is `Error`.
- Anything on the **award path** is gated behind `DetailedLogging` without
  exception — it runs at kill rate, and an ungated line there is a log flood.

---

## 5. Commands

### Pattern A: `Core.Command.RegisterCommand()` — how `Ranks` registers

FR-CMD-01 makes the command name, its permission, and whether it registers at
all operator-editable in `commands.jsonc`. **Attribute arguments are
compile-time constants, so `[Command]` cannot express any of that** — least of
all "an empty `Command` string means do not register". Registration is therefore
a loop over bound config in `Load`:

```csharp
foreach (var (key, info) in commands.CurrentValue.All)
{
    if (string.IsNullOrWhiteSpace(info.Command)) continue;   // operator removed it

    var guid = Core.Command.RegisterCommand(
        info.Command, handlers.For(key), registerRaw: true,
        permission: info.Permission,
        helpText: Core.Localizer[$"command.{key}.help"]);

    registered.Add(guid);

    foreach (var alias in info.Aliases)
        Core.Command.RegisterCommandAlias(info.Command, alias, registerRaw: true);
}
```

`RegisterCommand` returns a `Guid` for `UnregisterCommand`. Help text is
**mandatory** — one short sentence — and comes from `Core.Localizer`, not from
`commands.jsonc`, because it is player-facing (§6). `permission` is checked
before the handler runs, so an admin command needs no in-body check.
`registerRaw: true` keeps the console name equal to the configured one instead
of `sw_<name>`.

### Pattern B: `[Command]` attribute — fixed names only

Correct for any command whose name can never change. **`Ranks` has none**, so
this pattern does not appear in core; a module with hardcoded names may use it.

```csharp
[Command("rank", HelpText = "Shows your current rank, points and progress.")]
[CommandAlias("myrank")]
public void OnRank(ICommandContext context) { }
```

> A `[Command]` outside the `BasePlugin` class needs
> `Core.Registrator.Register(instance)` in `Load`, exactly like
> `[GameEventHandler]` (§7). An unregistered command does not error — it just
> does not exist, and the player sees `Unknown command`.

### Handler — `ICommandContext`

Members are `Sender` (`IPlayer?`), `IsSentByPlayer`, `Prefix`, `IsSlient`
(framework spelling — not `IsSilent`), `CommandName`, `Args`, `Reply` and
`ReplyAsync`. `Sender` is `null` from the server console, so check
`IsSentByPlayer` before dereferencing it.

> **Whether `Args[0]` is the first argument or the command name is not stated
> anywhere in the framework docs.** Confirm it once on a live server and fix the
> indexing everywhere — guessing wrong shifts every argument by one, which
> parses as a usage error rather than a crash. `CommandName` is available
> separately either way, so nothing needs `Args` to carry it.

```csharp
private void OnGivePoints(ICommandContext context)
{
    if (!context.IsSentByPlayer) return;
    var args = context.Args;

    if (args.Length < 2)
    {
        context.Reply("Usage: !givepoints <target> <amount>");
        return;
    }

    if (!int.TryParse(args[1], out var amount))
    {
        context.Reply(Core.Localizer["error.invalid_amount"]);
        return;
    }
}
```

### Permission naming

Format: `<pluginid>.<action>` — lowercase, dot-separated, `snake_case` for
multi-word actions. Each plugin uses **its own id** as the scope, so a module
namespaces under itself rather than under `ranks`.

```csharp
"ranks.admin"                  // core admin commands
"ranks.vip"                    // VIP multiplier flag
"ranks_weaponstats.view"       // owned by the WeaponStats module
```

CounterStrikeSharp-style permissions (`@css/...`) are **forbidden** — they are a
different framework's notation and SwiftlyS2 will not resolve them.

### Responses

```csharp
context.Reply("Message");                                   // back to chat or console
player.SendChat(Core.Localizer["prefix"] + " " + message);  // one player
Core.PlayerManager.SendChat(message);                       // everyone

// From a background thread, marshal first and re-validate
Core.Scheduler.NextTick(() =>
{
    if (player?.Controller is not { IsValid: true }) return;
    player.SendChat(message);
});
```

---

## 6. Translations / Localization

### Files

```
resources/translations/
  en.jsonc    ← REQUIRED, always complete
  <lang>.jsonc ← community-contributed
```

English must ship complete; every other language is optional. A missing key
echoes back `{lang}.{key}` as literal text, so a partial translation file
renders raw markers to players rather than falling back to English — keep
contributed files key-complete or do not ship them.

### Format

```jsonc
// resources/translations/en.jsonc
{
    "prefix": "[ [green]Ranks [default]]",
    "rank.current": "Your rank: {0} [default]— [gold]{1}[default] points",
    "rank.up": "[green]Rank up![default] You are now {0}[default].",
    "rank.down": "[red]Derank![default] You are now {0}[default].",
    "points.gain": "[lime]+{0}[default] points ([grey]{1}[default])",
    "points.loss": "[red]{0}[default] points ([grey]{1}[default])",
    "error.no_permission": "You don't have permission to use this command.",
    "error.invalid_amount": "Invalid amount."
}
```

Colour tags: `[default]`, `[darkred]`, `[green]`, `[lime]`, `[red]`, `[gold]`,
`[silver]`, `[blue]`, `[grey]`, `[yellow]`, `[lightblue]`, `[orange]`,
`[purple]`, `[magenta]`, `[lightred]`.

Placeholders use `string.Format` positional syntax (`{0}`, `{1}`). Always close
a colour run with `[default]`.

HTML — not chat brackets — is used for center messages and menu row text:

```jsonc
{ "center.rankup": "<span color=\"green\">Rank up!</span> <b>{0}</b>" }
```

### Which localizer

| API | Resolves |
|---|---|
| `Core.Localizer["key", args]` | The **server's** configured language |
| `Core.Translation.GetPlayerLocalizer(player)` | The **player's CS2 game language** |

**Default to `Core.Localizer`, and do not mix the two.**

`GetPlayerLocalizer` can only ever resolve a language CS2 itself ships. A server
running a community translation under a code CS2 does not have (Lithuanian is
the common example — CS2 has no `lt`) will silently get `en` for every player,
no matter what translation files are installed. `Core.Localizer` reads the
server default and is the only API that resolves custom language codes.

Per-player localization is attractive for an international plugin, and switching
to it is a legitimate project decision — but it must be **all or nothing**. Half
the strings following the server and half following the client is worse than
either.

```csharp
Core.Localizer["prefix"]
Core.Localizer["rank.up", rankName]
Core.Localizer["points.gain", amount, reason]
```

### Module translations

A module resolves from its **own** translation files first and falls back to
`Ranks`'. It must not write keys into the core files.

---

## 7. Events, Listeners & Hooks

Five distinct mechanisms. Pick deliberately.

> **Game events are a deprecated Source 2 feature and many of them do not fire.**
> The SwiftlyS2 documentation says so outright. Since nearly all of this
> plugin's scoring hangs off them, confirm an event actually fires on a live
> server before building a points value on it — a silent non-firing event looks
> exactly like a scoring bug. Entity hooks (`OnEntityTakeDamage`) and
> `IGameEventAccessor` raw field access are the fallbacks.
>
> Hook callback event objects are **temporary and valid only during the
> callback**. Copy anything needed later; never capture the event object.

### A. `[GameEventHandler]` — declarative CS2 game events

Unregistered automatically on unload. **Discovery runs per registered instance,
not per assembly:** the `BasePlugin` class is registered for you, and any other
class holding handlers needs `Core.Registrator.Register(instance)` in `Load`.
Register each instance once — a second call duplicates every callback on it.
Visibility does not matter; the signature must match the delegate exactly or the
method is silently skipped.

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnPlayerDeath(EventPlayerDeath @event)
{
    var victim = @event.Accessor.GetPlayer("userid");
    var attacker = @event.Accessor.GetPlayer("attacker");
    if (victim is not { IsValid: true }) return HookResult.Continue;
    // ...
    return HookResult.Continue;
}
```

### B. `Core.GameEvent.HookPre/HookPost` — imperative

Manual registration, unregistered automatically on unload. Use when registration
is conditional.

```csharp
Core.GameEvent.HookPost<EventPlayerHurt>(OnPlayerHurt);
Core.GameEvent.HookPre<EventPlayerDeath>(OnPlayerDeathPre);
```

> **These are not idempotent.** Register them in `Load()`, never in
> `UseSharedInterface` / `OnSharedInterfaceInjected` — see §8.

### C. `Core.Event` — framework lifecycle (manual `+=` / `-=`)

The framework cleans listeners up on unload and hot reload, so a missing `-=` is
a convention violation to fix, **not** a proven leak. Pair them anyway:

```csharp
private void RegisterListeners()
{
    Core.Event.OnClientPutInServer += OnClientPutInServer;
    Core.Event.OnClientDisconnected += OnClientDisconnected;
    Core.Event.OnMapLoad += OnMapLoad;
}

private void UnregisterListeners()
{
    Core.Event.OnClientPutInServer -= OnClientPutInServer;
    Core.Event.OnClientDisconnected -= OnClientDisconnected;
    Core.Event.OnMapLoad -= OnMapLoad;
}

public override void Unload() => UnregisterListeners();
```

Common members: `OnClientConnected`, `OnClientPutInServer`,
`OnClientDisconnected`, `OnMapLoad`, `OnMapUnload`, `OnStartupServer`,
`OnEntityCreated`, `OnEntityDeleted`, `OnEntityTakeDamage`,
`OnConVarValueChanged`, `OnTick`, `OnWorldUpdate`.

This list **drifts** — the authoritative set is
`@sd get_document("swiftlys2/docs-api-events-ieventsubscriber.md")`, with
delegate signatures under `EventDelegates`. `OnTick` and `OnWorldUpdate` take no
parameter and are hot paths (`OnWorldUpdate` runs during hibernation too) — keep
them allocation-free.

### D. `[EventListener<T>]` — declarative framework events

```csharp
[EventListener<EventDelegates.OnClientSteamAuthorize>]
public void OnClientSteamAuthorize(IOnClientSteamAuthorizeEvent @event) { }
```

### E. Special hook attributes

```csharp
[ClientChatHookHandler]
public HookResult OnClientChat(int playerId, string message, bool teamonly) => HookResult.Continue;

[ClientCommandHookHandler]
public HookResult OnClientCommand(int playerId, string commandLine) => HookResult.Continue;
```

### HookResult — `Handled` does **not** block the original

The most common misreading in the framework. Returning `Handled` when you meant
to cancel lets the original run anyway, silently.

| Value | Following hooks | Original function |
|-------|-----------------|-------------------|
| `Continue` | run | runs |
| `Handled` | cancelled | **runs** |
| `CancelOriginal` | run | cancelled |
| `Stop` | cancelled | cancelled |

`Handled` and `Stop` **do nothing in post hooks** — the original has already
run. To block something, hook `Pre` and return `Stop`, or `CancelOriginal` if
other plugins' hooks should still see it.

### Which to use

| Mechanism | Auto-unregister | Use when |
|-----------|----------------|----------|
| `[GameEventHandler]` | yes | Standard CS2 game events |
| `Core.GameEvent.HookPre/Post` | yes | Conditional/dynamic registration |
| `Core.Event +=/-=` | yes (but `-=` by convention) | Framework lifecycle, imperative |
| `[EventListener<T>]` | yes | Framework lifecycle, declarative (preferred) |

---

## 8. Shared Interfaces

`Ranks` is primarily a **provider**. The contract is the product — treat a
breaking change to it as a breaking change to every module in the ecosystem.

> **The dependency runs one way.** Core never calls module code, never holds a
> module-supplied delegate, and never gains config keys because a module was
> installed. The contract therefore exposes **facts and operations only** — read
> a record, change points, persist, observe, and read core's own settings. If a
> new method would have core invoking something a module supplied, it belongs on
> the module's side of the line instead. See the requirements doc, FR-PTS-01.

### Contract project

```csharp
// Ranks.Contract/IRanksApi.cs
namespace Ranks.Contract;

public interface IRanksApi
{
    void AddPoints(ulong steamId, int amount, string? reason = null);
    long GetStat(ulong steamId, string statKey);
    RanksPointSettings GetPointSettings();
    string ResolveTableName(string baseName);
    event Action<RanksPointsChangedArgs>? PointsChanged;
    // full surface: docs/functional-requirements.md §11
}
```

Interfaces, DTOs and enums only — no implementation, no framework dependencies
beyond `IPlayer`.

### Registering (provider side)

```csharp
public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
{
    interfaceManager.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);
}
```

`Load()` runs **before** `ConfigureSharedInterface()`, so build the
implementation in `Load` and register the already-built instance here.

`AddSharedInterface` throws on a duplicate key, but the rebuild disposes the
manager (clearing the key set) before re-running registration, so
re-registration is safe.

### ⚠️ The handshake re-runs constantly

`PluginManager.RebuildSharedServices()` disposes and rebuilds the whole
interface manager, then re-runs `ConfigureSharedInterface` →
`UseSharedInterface` → `OnSharedInterfaceInjected` for **every loaded plugin**.
It fires on every plugin load **and every unload**.

Three consequences:

1. **Nothing non-idempotent goes in the handshake.** A `Core.GameEvent.HookPost`
   or a bare `+=` there accumulates one live registration per rebuild — on a
   busy server that is N duplicate handlers per event. Hooks belong in `Load()`.
   Idempotent registration (registering an event descriptor, a menu entry) is
   fine.

   > **Confirmed live in official code.** Several `SwiftlyS2-Plugins/VIPCore`
   > modules reset `_isFeatureRegistered = false` at the top of
   > `UseSharedInterface` and then re-run a `RegisterVipFeatures()` that ends
   > with `_vipApi.PlayerLoaded += …` / `OnPlayerSpawn += …`. The
   > `RegisterFeature` call itself is idempotent — core overwrites
   > `_features[key]` — but the `+=` lines are not, and they are only removed in
   > `Unload`. Do not copy that shape: subscribe in `Load`, or guard with a flag
   > that the handshake does **not** reset.
2. **Binders must be fail-closed**, never early-return — an early return leaves
   a stale reference pointing at a disposed provider.
3. **Never log from a binder.** It runs per rebuild and will spam. Report
   absence from `OnAllPluginsLoaded`, which runs once per load cycle and is the
   only place where field state is authoritative.

### Consuming (module side)

```csharp
private object? _ranksApi;            // NOT IRanksApi? — see below
private volatile bool _available;

public override void UseSharedInterface(IInterfaceManager im) => Acquire(im);
public override void OnSharedInterfaceInjected(IInterfaceManager im) => Acquire(im);

public override void OnAllPluginsLoaded()
{
    if (!_available)
        Core.Logger.LogError("Ranks API not found — this module is disabled.");
}
```

Bind in **both** `UseSharedInterface` and `OnSharedInterfaceInjected`: the first
covers providers already loaded, the second covers providers that load later.

### ⚠️ An absent contract DLL kills the entire host

For a publicly distributed plugin, **every** consumed interface is a soft
dependency. You cannot know what is installed on someone else's server, so there
is no "it's always there" shortcut.

The CLR resolves every type a method *names* when it JITs that method, not when
execution reaches the line. On a server without the provider:

- a `TryGetSharedInterface<IRanksApi>(...)` call site — **or merely a field
  typed `IRanksApi?`** — throws `FileNotFoundException` when its enclosing
  method is prepared;
- the throw surfaces at the **caller's** frame, so the method's own try/catch
  never sees it.

**Which callback it escapes from decides the blast radius**, and only one of the
two is survivable (verified in `PluginManager.cs`):

| Escapes from | Guarded by core? | Result |
|---|---|---|
| `Load` | `LoadPluginInternal` wraps it in `try`/`catch` → `CleanupFailedPlugin` + `FailWithError` | **Your plugin only** is marked `Error` |
| `ConfigureSharedInterface`, `UseSharedInterface`, `OnSharedInterfaceInjected` | **No.** `RebuildSharedServices` runs all three loops bare | Propagates through `LoadPlugins` → `Initialize` → `StartupService`'s constructor → `Hosting failed to start`, **every plugin on the box** |
| `OnAllPluginsLoaded` | **No.** `NotifyAllPluginsLoaded` is bare | Same as above |

Acquisition happens in `UseSharedInterface` / `OnSharedInterfaceInjected`, i.e.
in the unguarded half. That is why the `try`/`catch` has to be there and cannot
be deferred to `Load`.

`Try…` / `Has…` look defensive and are not. Isolate instead — nothing outside
the `[NoInlining]` method may name the contract type, and the `try` goes in the
**caller**, which is the frame the throw actually lands in:

```csharp
private void Acquire(IInterfaceManager im)   // names no contract type
{
    try { _bridge.TryAcquire(im); }
    catch (Exception ex) { logger.LogDebug(ex, "contract not resolvable"); }
}

[MethodImpl(MethodImplOptions.NoInlining)]
public bool TryAcquire(IInterfaceManager im)
{
    try
    {
        if (!im.TryGetSharedInterface<IRanksApi>("Ranks.Api.v1", out var api) || api is null)
        {
            _api = null; _available = false;
            return false;
        }

        _api = api; _available = true;
        Attach(api);
        return true;
    }
    catch (Exception ex)   // runtime faults inside Attach — never the JIT throw
    {
        logger.LogWarning(ex, "could not acquire the Ranks API");
        _api = null; _available = false;
        return false;
    }
}
```

`TryGetSharedInterface` subsumes the `Has…` probe: one call, no window between
check and fetch, `out` null on a miss, one place to fail closed. The caller's
catch is **unfiltered** on purpose — filtering to
`FileNotFoundException or TypeLoadException or BadImageFormatException` lets
anything unforeseen through, and this path's whole job is to never fail the host.

Every other method reads `_available` (a `bool` needs no contract resolution)
and routes typed calls through its own `[NoInlining]` helper.

**`Unload` is the classic miss.** `_api.PointsChanged -= handler` needs the
contract loaded to unwire, and its in-body try/catch cannot catch its own
JIT-time throw — give it the same isolated-helper treatment. Core catches it
there, so the cost is a failed unload and a plugin left `Indeterminate` rather
than a dead host. When auditing, grep the **field name** across the whole
plugin: the acquire path is the obvious site and never the only one. A stray
`if (_ranksApi != null)` in a summary log line throws just as readily, and the
table above says what it costs depending on where it runs.

### Referencing the contract

Modules reference `Ranks.Contract` with `<Private>false</Private>` and
`<ExcludeAssets>runtime</ExcludeAssets>`, and **never ship their own copy** —
two copies mean two Type identities for one interface, which breaks the
cross-plugin cast inside `TryGetSharedInterface<T>`.

The provider ships the contract DLL in `resources/exports/`.

> **Do not add `resources/exports` to a static `<Content Include>` glob.** Item
> globs are evaluated during project evaluation, *before* any target runs, so a
> build-generated contract DLL is not on disk yet. It works on a dev machine (a
> previous build left the file behind) and ships **nothing** from a clean CI
> checkout — producing a `ReflectionTypeLoadException` at runtime with no build
> failure. Copy it in from an explicit `<Copy>` target at `AfterTargets="Build"`
> and `AfterTargets="Publish"` instead.

### Versioning

- Key format `Ranks.Api.v1`. Breaking changes bump to `.v2`, and the old
  interface stays registered for at least one minor release.
- Additive changes (new method on a new interface, new optional DTO field) do
  not bump the key.
- **One interface, one key — do not split the surface per concern.** Unprefixed
  and **lowercase `v`** follow the stable *Shared API* guide
  (`PluginName.ServiceName.v1`), which `VIPCore` matches and `Admins` does not.
  Not splitting is a deliberate divergence from `Admins.Core`, which publishes
  five — the cost shows up in its consumers as five nested
  `try` / `HasSharedInterface` / `LogWarning` blocks per module, each needing
  the fail-closed binding right independently. See FR-API-01.

---

## 9. Thread Safety & Async Data Access

### The rule

**Game state — players, entities, pawns, controllers — is only safe to touch
from the main thread.** Background work must marshal back before reading any of
it.

`Core.IsGameThread` answers "am I on it right now", which is worth asserting at
the top of any helper that both hot paths and background continuations can
reach — the failure mode otherwise is a rare corruption with no stack pointing
at the caller.

### `Task.Run` + `Core.Scheduler.NextTick`

```csharp
var steamId = player.SteamID;        // capture primitives BEFORE the hop

Task.Run(async () =>
{
    try
    {
        var record = await LoadRecordAsync(steamId);

        Core.Scheduler.NextTick(() =>
        {
            // Re-resolve and re-validate — the player may have left.
            var p = Core.PlayerManager.GetAllPlayers()
                .FirstOrDefault(x => x.SteamID == steamId);
            if (p?.Controller is not { IsValid: true }) return;
            _cache[steamId] = record;
        });
    }
    catch (Exception ex) { logger.LogError(ex, "Load failed for {SteamId}", steamId); }
});
```

Never capture an `IPlayer` across the marshal — capture the SteamID and
re-resolve.

### `NextWorldUpdate` — entity/pawn modification

```csharp
Core.Scheduler.NextWorldUpdate(() =>
{
    var pawn = player.PlayerPawn;
    if (pawn is null) return;
    // ...
});
```

### `NextWorldUpdateAsync` — main-thread data from an async context

```csharp
var players = await Core.Scheduler.NextWorldUpdateAsync(() =>
    Core.PlayerManager.GetAllValidPlayers()
        .Where(p => !p.IsFakeClient)
        .Select(p => p.SteamID)
        .ToList());

foreach (var steamId in players)
    await SaveRecordAsync(steamId);
```

### ⚠️ `Repeat*` fires the first callback immediately

```csharp
Core.Scheduler.DelayBySeconds(5, () => { });                      // once, after 5s
Core.Scheduler.RepeatBySeconds(1.0f, () => { });                  // NOW, then every 1s
Core.Scheduler.DelayAndRepeatBySeconds(300, 300, SavePass);       // after 300s, then every 300s

Core.Scheduler.StopOnMapChange(timer);
timer?.Cancel();
```

Nothing at the call site hints at this. A save pass, a purge, or anything that
walks entities almost always wants the **delayed** form — `RepeatBySeconds`
would run it during `Load()`, mid map-load, on every single restart.

---

## 10. Data Management & Optimization

### Concurrent collections for shared state

```csharp
private readonly ConcurrentDictionary<ulong, PlayerRecord> _records = new();
```

A plain `Dictionary` touched from both a save task and the game thread will
corrupt — and the symptom (`IndexOutOfRangeException` inside `TryInsert`, or a
`KeyNotFoundException` for a key that was definitely added) does not look like a
threading bug.

### Slot-indexed lookup on hot paths

Kill, hurt and damage events fire many times per second. Resolve per-player
state from a **slot-indexed array holding the record reference directly** —
measured 2.8–3.3 ns/op, against 13.2 for a `ConcurrentDictionary` and 17.7–19.1
for a `ConditionalWeakTable`.

> **Do not reach for `ConditionalWeakTable<IPlayer, T>`** even though SwiftlyS2's
> own `Fixes` plugin uses it. It is the slowest option measured, and its
> `IPlayer` key cannot find a departed record on reconnect — which is what
> FR-DB-07 requires — so the SteamID dictionary has to exist anyway. Rationale
> and numbers: requirements doc §12, design note.

Keep the SteamID-keyed dictionary as the authority for anything off the hot
path, and for records whose player has disconnected but not yet been saved. The
slot array must be **cleared on disconnect**, or it serves a record the
dictionary has already released.

### Save queue + saved-value snapshot

Writes are absolute (requirements doc FR-DB-06), so there is no dirty-flag
bookkeeping at mutation sites — a record diffs against the values its last
*committed* write sent.

```csharp
private readonly HashSet<ulong> _saveQueue = new();      // idempotent by construction
private readonly object _saveQueueLock = new();

internal void EnqueueSave(ulong steamId)
{
    lock (_saveQueueLock) { _saveQueue.Add(steamId); }
}

private void StartSaveProcessor(int intervalSeconds)
{
    // Delayed form: Repeat* would flush an empty queue during Load().
    _saveCts = Core.Scheduler.DelayAndRepeatBySeconds(intervalSeconds, intervalSeconds,
        () => Task.Run(SavePassAsync));
}
```

### Disconnect handling

A departed record stays resident until it is fully saved, and a reconnect adopts
it. The disconnect handler must **not** gate on `IsValid` — the pawn is already
gone by then (§12).

```csharp
private void OnClientDisconnected(ulong steamId)
{
    EnqueueSave(steamId);     // eviction happens after the save confirms, not here
}
```

### Reflection caching

```csharp
private static readonly ConcurrentDictionary<Type, PropertyInfo?> _propertyCache = new();
var prop = _propertyCache.GetOrAdd(typeof(T), static t => t.GetProperty("Accessor"));
```

---

## 11. Menus & UI

### Use `Core.Menu`, not `Core.MenusAPI`

`Core.MenusAPI` (`IMenuManagerAPI`, `IMenuBuilderAPI`, `IMenuAPI`, `IMenuOption`)
is **`[Obsolete]` since 1.5.0** — *"the classic menu system is deprecated and will
be removed in future versions."* Every menu in this repo is built through
`Core.Menu` (`IMenuService`, `IMenuBuilder`, `IMenu`, `IMenuSession`), and new
code must be too (FR-CMD-06).

### Building

```csharp
_core.Menu.CreateMenu("ranks.root")          // id doubles as the keybind scope
    .WithTitle(_core.Localizer["menu.root.title"])
    .AddHeader(new DividerComponent())       // WithTitle draws no rule of its own
    .WithItemsPerPage(8)
    .Add(new TextComponent(_core.Localizer["menu.rank.XP", record.Points]))
    .Add(new SubmenuComponent(_core.Localizer["menu.root.stats"], _ => BuildStatistics(player)))
    .Build()
    .Open(player);
```

Ids are namespaced (`"ranks.root"`, `"ranks.stats.main"`) because a menu id is
also its keybind configuration scope — **a duplicate id means the server owner's
binds for one menu silently apply to the other.**

`WithTitle` only *"adds a title line to the header"*; the rule under it is a
`DividerComponent` you add yourself. Every menu here carries one at each end —
`AddHeader` after the title, `AddFooter` before `Build()` — which separates the
body from the title above and the key hints below.

Counting the two calls is **not** a way to check this. A builder with several
early returns — `PrestigeMenus.Build` is the one here — calls `AddHeader` once on
the shared builder and then `AddFooter` on each branch, so the file reads 1 header
against 3 footers and 3 builds while every menu it produces still renders exactly
one of each. Only one branch runs per call. Read the branches before believing a
count.

### Components

Don't hand-roll a widget out of `ButtonComponent`:

| Component | Use |
|---|---|
| `TextComponent` | non-interactive line |
| `ButtonComponent` | clickable action |
| `ToggleComponent` | on/off, with `ctx.NewValue` |
| `SliderComponent` | numeric range |
| `ChoiceComponent` / `SelectorComponent<T>` | cycle through values |
| `InputComponent` | chat input |
| `ProgressBarComponent` | progress, from a `Func` so it stays live |
| `SubmenuComponent` | opens another menu |
| `PagedCollectionComponent<T>` | async list — paging is built in |
| `DividerComponent`, `SpacerComponent`, `TitleComponent` | layout |

A preference menu uses `ToggleComponent`, not buttons that flip a flag.

### Nesting

**`SubmenuComponent`, never `ButtonComponent` + `Open()`.** `Open()` starts a new
root with no back-stack, so the player's back key has nothing to return to:

```csharp
.Add(new SubmenuComponent(label, _ => BuildChild(player)))   // lazy: rebuilt per open
```

The factory form is what you want for anything whose contents can change. A menu
with **no parent of its own is adopted by the entry that opened it** — the same
mechanism that lets a module's menu (FR-CMD-04) sit under core's Statistics and
still come back.

**Adoption only covers `SubmenuComponent`.** A menu opened from a row handler —
`PagedCollectionComponent.OnItemSelected`, a `ButtonComponent` — has to be told:

```csharp
OnItemSelected = ctx =>
{
    var child = BuildDetail(ctx.Player, ctx.Item);
    child.Parent = ctx.Session.Menu;   // without this, back closes the tree
    child.Open(ctx.Player);
}
```

### Loading data

The factory runs on the **game thread and must not block**. Anything that reads
the database goes inside `PagedCollectionComponent`, which owns its own thread
and already pages:

```csharp
.Add(new PagedCollectionComponent<RanksLeaderboardEntry>(async () =>
{
    try   { return await _api.GetTopAsync("points", 50).ConfigureAwait(false); }
    catch (Exception ex) { _logger.LogError(ex, "..."); throw; }   // rethrow -> FailedText
})
{
    Formatter  = e => _core.Localizer["menu.top.row", e.Position, e.Name, e.Value],
    EmptyText  = _core.Localizer["menu.top.empty"],
    FailedText = _core.Localizer["menu.top.error"],
})
```

**`TItem` is the row type, never `IMenuComponent`.** The component renders each
item through `Formatter`, which defaults to `ToString()` — so handing it
`TextComponent`s draws `SwiftlyS2.Shared.Menu.Components.TextComponent` on every
line. Both this repo's leaderboard and its module menus shipped that way once.

`EmptyText` and `FailedText` cover the no-rows and threw cases, so the loader
returns data only. Catch to **log and rethrow** — swallowing the exception shows
the empty message for a failure.

### Lifetime

- **One open menu per player, across every plugin.** Opening yours closes someone
  else's; that is normal, not an error to guard against.
- **Released automatically on unload** — each plugin's menus are attributed to it
  — so `Unload` needs no menu teardown.
- A **module's contributed entry is a delegate core holds**, not a menu core owns,
  so it needs explicit unregistration (`RanksModuleBase.Unregister` does it).

Menu colour markup is **HTML tags, never chat brackets** (§6). A row is one
centred line and clips at roughly **42 characters** — anything longer belongs in
chat.

### Server configuration is not ours

Key binds, renderer and input method are the **server owner's**, and they live in
`configs/menus/menus.jsonc` — **not** `core.jsonc`, and not in any Ranks config.
The `"core.menu"` block inside that file is the section every menu inherits; its
name has nothing to do with `core.jsonc`, which is the usual mix-up.

Resolution is highest-priority-first, and the server file wins outright:

| Priority | Source |
|---|---|
| `Global` (100) | `configs/menus/menus.jsonc` |
| `Plugin` (50) | `WithKeybindSource` / `WithInputMethodSource` / `WithRendererSource` |
| `CodeDefault` (0) | `WithInputMethod` / `WithRenderer` / the action's `DefaultKey` |

**This repo sets none of them**, deliberately — so nothing here can shadow an
operator's choice, and "the config is being ignored" is never a Ranks bug. Check
the file path first, then that the menu id matches, then that the menu uses the
`Buttons` input method (binds do not apply under `Chat`).

### Clan tag updates (scoreboard)

Three steps, all required — without the last two the scoreboard will not reflect
the change until the next natural refresh:

```csharp
using SwiftlyS2.Shared.GameEventDefinitions;

player.Controller.Clan = rankTag;
player.Controller.ClanUpdated();
if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
    Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
```

Do **not** re-write the tag every tick. Other plugins write the same field with
no arbitration, so a tight loop produces flicker rather than ownership.

---

## 12. Player Validation

### `IsValid` is not "not null" — it requires a pawn

The API doc for `IPlayer.IsValid` reads: *"Checks if the player is valid (has
controller, is not HLTV, is connected **and has pawn**)."* That last clause
decides which predicate you need, and getting it wrong fails **silently** — the
handler simply never runs.

| Context | Predicate |
|---|---|
| Spawn, death, damage, weapons, any pawn work | `player is { IsValid: true }` |
| Connect/join, disconnect, chat, menus, map load | `player?.Controller is { IsValid: true }` |

A player who is connecting, sitting in team-select, spectating between rounds,
or disconnecting has a controller but **no pawn**. Gating a join handler on
`IsValid` is why a plugin "does nothing on first connect" — and gating a
*disconnect* handler on it is why records silently fail to save.

```csharp
internal static bool IsPlayable(IPlayer? p)  => p is { IsValid: true, IsFakeClient: false };
internal static bool IsConnected(IPlayer? p) => p?.Controller is { IsValid: true } && !p.IsFakeClient;
```

At join time the real SteamID is not available until Steam has authorized the
client — `IPlayer.UnauthorizedSteamID` holds the provisional value until then,
and a record keyed on the wrong id is a record lost. So there are two distinct
needs, and conflating them produces a polling loop:

- **"When should I load?"** → subscribe to the **`OnClientSteamAuthorize`** core
  event, with `OnClientSteamAuthorizeFail` as its paired failure signal. Never
  poll `IsAuthorized` on a timer or a tick.
- **"Is this SteamID safe to read right now?"** → check `IPlayer.IsAuthorized`
  at the point of use.

> The property is `IPlayer.Name`, **not** `PlayerName`. Several SwiftlyS2 guide
> pages still show `player.PlayerName`; the API reference does not, and it does
> not compile.

### Common patterns

```csharp
// Pawn-having non-bots. GetAllValidPlayers() already applies IsValid.
Core.PlayerManager.GetAllValidPlayers().Where(p => !p.IsFakeClient).ToList();

// Everyone connected, pawn or not — announcements, menus, disconnect cleanup.
Core.PlayerManager.GetAllPlayers().Where(IsConnected).ToList();

// Hot reload — adopt already-connected players.
if (hotReload)
{
    foreach (var p in Core.PlayerManager.GetAllPlayers().Where(IsConnected))
    {
        var steamId = p.SteamID;             // capture BEFORE the hop
        Task.Run(() => LoadRecordAsync(steamId));
    }
}
```

Bots are excluded from scoring by `Rank.BotsEnabled`, but note that a bot has
no meaningful SteamID — never create a record for one.

---

## 13. Plugin Lifecycle

### Verified order

```
Constructor  →  Load(hotReload)  →  ConfigureSharedInterface()
             →  UseSharedInterface()  →  OnSharedInterfaceInjected()
             →  OnAllPluginsLoaded()  →  [runtime]  →  Unload()
```

**`Load()` runs BEFORE `ConfigureSharedInterface()`.** Source:
`PluginManager.LoadPluginInternal` calls `plugin.Load(hotReload)`;
`LoadPlugins()` then calls `RebuildSharedServices()` followed by
`NotifyAllPluginsLoaded()`.

This matters: it is what makes "build the API implementation in `Load`, register
the already-built instance in `ConfigureSharedInterface`" correct. Several
community docs state the reverse — don't trust that claim if it resurfaces.

`RebuildSharedServices()` re-runs the three handshake callbacks for every loaded
plugin on every load **and** unload, so `ConfigureSharedInterface` →
`OnSharedInterfaceInjected` fire many times per session while `Load` and
`OnAllPluginsLoaded` fire once per load cycle.

### Do NOT null-check shared-interface fields in `Load`

On the hot-reload path, `Load` runs before the framework has finished re-wiring
shared-interface fields, so every field reads null even when the provider is
loaded and healthy. `OnAllPluginsLoaded` is the only place where that state is
authoritative.

### Full skeleton

```csharp
[PluginMetadata(Id = "Ranks", Version = "1.0.0", Name = "Ranks",
    Author = "Shmitzas", Description = "Point-based progression and statistics system.")]
public partial class Ranks : BasePlugin
{
    public static new ISwiftlyCore Core { get; private set; } = null!;
    private ILogger<Ranks> logger = null!;
    private IOptionsMonitor<Config> cfg = null!;
    private RanksApi _api = null!;
    private CancellationTokenSource? _saveCts;

    public Ranks(ISwiftlyCore core) : base(core) { }   // empty

    public override void Load(bool hotReload)
    {
        Core = base.Core;
        LoadConfiguration();
        InitializeDatabase();
        _api = new RanksApi(/* services */);
        RegisterCoreEvents();                 // populate the event registry
        RegisterListeners();
        RegisterCommands();

        _saveCts = Core.Scheduler.DelayAndRepeatBySeconds(
            cfg.CurrentValue.Database.SaveIntervalSeconds,
            cfg.CurrentValue.Database.SaveIntervalSeconds,
            () => Task.Run(SavePassAsync));

        if (hotReload)
            foreach (var p in Core.PlayerManager.GetAllPlayers().Where(IsConnected))
            {
                var steamId = p.SteamID;
                Task.Run(() => LoadRecordAsync(steamId));
            }
    }

    public override void ConfigureSharedInterface(IInterfaceManager im)
        => im.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);

    public override void OnAllPluginsLoaded()
    {
        if (cfg.CurrentValue.General.DetailedLogging)
            logger.LogInformation("Ranks ready on connection {Connection}.", _api.ConnectionName);

        if (!cfg.CurrentValue.Rank.UseCoreRankingLogic)
            logger.LogWarning("Core ranking logic is disabled — a rating module must write points.");
    }

    public override void Unload()
    {
        UnregisterListeners();
        _saveCts?.Cancel();
        FlushPendingSaves();     // blocking, bounded budget (~8s)
    }
}
```

`Unload` must flush. An absolute-write model with an unflushed queue loses
whatever has accumulated since the last pass.

---

## 14. Auditing & Bug Prevention

### Silent invariants

These rules used to be asserted by load-time self-checks. The checks are gone —
they were pure, so they could only ever report what the author's own first run
already showed, while a wrong assertion would stop the plugin loading on every
server at once. The rules themselves did not go away, and **none of them fails
visibly**: breaking one produces a plausible-looking result, not an error. Verify
them by reading when you touch the logic, and state in your report which ones the
change could have affected.

| Area | Invariant | Why it is silent |
|---|---|---|
| `TableNames` | The plugin supplies the separator, not the operator. Empty affixes change nothing. A module ledger drops the `ranks` prefix. Overlong identifiers fold deterministically | A wrong affix writes to a real table that is simply the wrong one, and a ledger collision reports migrations as applied that never ran |
| `AwardPipeline.ShouldRecordDeath` | The warmup / bot gate is a five-input truth table: warmup deaths drop when `WarmupPoints` is off, bot deaths when `BotsEnabled` is off | Scoring just runs slightly wrong, forever |
| Scoring anchors | Equal players score the configured value; the difference is bounded and symmetric; the ladder picks the highest threshold met | A broken anchor makes every configured `kill` value mean something other than what it says |
| `ScoreboardService.EngineRankType` | Every visible mode maps to a **confirmed** engine rank type. `0` is the engine's "no rank" | CS2 renders nothing for an unrecognised `m_iCompetitiveRankType` and reports no error, so it looks like the feature is switched off. It shipped broken once, writing `(byte)mode` |
| `ScoreboardService.ResolveMode` | An unparseable or undefined mode falls back to the configured mode, never to `Off`. `Enum.TryParse` accepts numbers, so `Enum.IsDefined` is required | A module typo would otherwise blank every scoreboard on the server |
| `CustomIcon` | At the shipped defaults, ladder position 1 lands on icon 0 and saturates past 18; a margin larger than the position must clamp at 0 | The engine treats the value as an array index |
| `EloMath` | `Expected(r,r) == 0.5` and `Expected(a,b) + Expected(b,a) == 1`; zero evidence moves nothing; shrinkage is monotonic in `n`; the envelope clamp holds on a **corrupted** accrual; the sign floor cannot turn a win into a loss; the penalty is a flat deduction; ties share a percentile rank | A sign error is invisible on even teams, a missing clamp only shows on a lopsided match, and a wrong shrinkage weight just moves the ladder at the wrong speed. The clamp is unreachable on valid data — a 20 000-iteration random sweep passed with it deleted |
| `PrestigeLadder` | Thresholds resolve to the highest level reached; crossing one resets the display to zero; operator order and duplicates do not change the outcome | A wrong level silently shows the wrong number to the player |
| `ComboMatcher` | Closest match wins, ties go to config order, `RequiresNone` excludes, a count combo needs the whole run and keeps accumulating after a loss, weapon class accepts both spellings | A combo that never fires looks like the operator configured it wrong |
| Weapon names | Both event spellings land on one row; non-weapon sources keep their bare name; unusable values become `unknown` | Stats split silently across two rows for the same gun |
| `MenuEntryRegistry` | A module's entries die with it, and re-registering the same `(moduleId, Section, Label)` replaces rather than appends | The handshake re-runs on every other plugin's load, so an append shows the row once per reload; an entry left behind is a delegate into an unloaded assembly load context, which crashes only when someone opens that menu |
| Menu nesting | Every parent-child link is a `SubmenuComponent`, never `ButtonComponent` + `Open()` | `Open()` starts a new root with no back-stack. The menu looks right until the player presses back and the whole tree closes |
| `PlayerReset` | Zeroes **every** counter, including `Headshots`, `Hits` and `Misses` | A forgotten column leaves a "reset" player carrying their old accuracy, which only shows up on a stat nobody checks first. The command's copy already had this bug before the two were merged |
| Accuracy | `Hits / (Hits + Misses)`, never `Hits / Shots` | One shotgun blast is a single trigger pull that lands several hits, so a shots-based figure quietly exceeds 100% |
| `PagedCollectionComponent<TItem>` | `TItem` is the **row type**, and the text comes from `Formatter` | The default formatter is `ToString()`, so passing `IMenuComponent` items compiles, runs, pages correctly — and draws `SwiftlyS2.Shared.Menu.Components.TextComponent` on every line. Shipped that way in the leaderboard and all three module menus |
| Menu row width | A row is one centred line and clips at roughly **42 characters** | Both ends are cut off with no ellipsis, so a too-long row looks like corrupted text rather than a layout problem. Sentences go to chat |
| `IMenu.Parent` | A menu opened from a **row handler** must have `Parent` set by hand | Adoption only happens for a menu a `SubmenuComponent` opened. From `OnItemSelected` the child opens fine and looks right — the break only shows when the player presses back and the whole tree closes |
| Bridge calls from event paths | Anything reachable from a `[GameEventHandler]`, a `Registrator` registration or a scheduler callback must check `_bridge.IsAvailable` before touching the bridge | `IsAvailable` is a plain bool and resolves nothing; every other bridge member names a contract type, so with no Ranks installed the CLR fails to JIT it and throws into the *caller's* frame — a game-event or `NextTick` body, where nothing catches it. WeaponStats avoids this structurally by keeping its collector contract-free; a module whose tracker calls the bridge needs the guard instead |
| Module idle warning | Report absence from `OnAllPluginsLoaded`, never from the `UseSharedInterface` / `OnSharedInterfaceInjected` path | That handshake re-runs on every plugin load and unload, so warning there repeats once per rebuild rather than once per load cycle |
| Menu handler threading | A handler that calls a **thread-unsafe** API (`SendChat`, scoreboard/controller writes) goes through `RankMenus.OnGameThread` | A handler is not guaranteed to run on the game thread. The classic menu API dispatched every click on the thread pool and took a production server down that way; `Core.IsGameThread` + `Scheduler.NextTick` settles it without needing to know which. Framework calls like `IMenu.Open` are *not* in this class and are left alone |

### Where checking belongs instead

- **Operator input is a different category and still gets validated at runtime.**
  `ConfigValidator` stays: numbers clamp and warn, but table affixes **throw**
  against `^[A-Za-z0-9_]*$` because an identifier cannot be parameterised and is
  concatenated into DDL. That is a SQL-injection boundary, not a self-check.
- Do **not** reintroduce assertion classes, `.selfcheck` harness projects, or a
  test project. If a rule above is genuinely load-bearing and cheap, document it
  here and keep the code shaped so it reads correctly.

### How to run an audit

Every item below is an **enumeration, not an affirmation**. "All `Core.Event +=`
have a matching `-=`" is a property you satisfy by glancing at `Unload()`,
seeing a couple of `-=` lines, and feeling right. "Print every `+=` site next to
the line number of its `-=`" cannot be passed by feel: a missing one is an empty
cell in a table you have to show.

**Build the list, check each row, show the list.** If you did not enumerate, you
did not audit — report the item as unchecked rather than ticking it.

Three ordering rules that matter as much as the items:

- **Inventory before symptoms.** When the request arrives with bug reports
  attached, build item 1 *first* and read the reports *after*. Symptoms anchor
  attention; chasing two named bugs turns the rest of the plugin into a drive-by
  read, which is how a first pass misses what a second pass finds.
- **The call site lies about timing.** `RepeatBySeconds(3600, Purge)` reads as
  "hourly" and means "now, then hourly". Nothing local hints at it. Timing,
  re-entrancy and lifecycle semantics come from `@sd`, never from the shape of
  the call.
- **Some bugs are not in this plugin.** Command aliases, shared-interface keys
  and config filenames live in a namespace shared with every other plugin on the
  server. They cannot be validated from inside one folder.

### Checklist

1. **Surface inventory — build this first; everything below walks it**
   - [ ] Every command and alias (`[Command]`, `[CommandAlias]`, **and**
         config-driven `RegisterCommandAlias`)
   - [ ] Every menu and every selectable row
   - [ ] Every game-event hook and `Core.Event` handler
   - [ ] Every timer / repeating scheduler task
   - [ ] Every registered core event key and its default points
   - [ ] Public API surface published, and anything consumed
   - [ ] Every config key, with default and clamp
   - [ ] Every table, column and index

2. **Framework-semantics audit** — the call site is not evidence
   - [ ] Every scheduler call looked up in `@sd`. `Repeat*` fires the first
         callback **immediately**; `DelayAndRepeat*` is the one that waits
   - [ ] Nothing non-idempotent registered in the shared-interface handshake
   - [ ] `IsValid` semantics confirmed per use — it requires a pawn, so it is
         wrong in join, disconnect, chat and menu handlers
   - [ ] Every `HookResult` checked against the table in §7 — `Handled` does not
         block the original
   - [ ] Return values of framework calls that can fail are actually read

3. **Registration / teardown audit** — produce the table
   - [ ] Table: every class holding a `[Command]`, `[GameEventHandler]`,
         `[EventListener<T>]` or any other listener attribute → the
         `Core.Registrator.Register` line that registers it. Only the
         `BasePlugin` class is exempt. An unregistered handler never fires and
         never errors, so an empty cell here is a dead feature
   - [ ] Each of those instances registered **exactly once** — a repeat call
         duplicates every callback on it, and the handshake re-runs per rebuild
   - [ ] Table: every `Core.Event +=` → line number of its `-=` in `Unload()`
   - [ ] Table: every timer/CTS → where it is cancelled
   - [ ] Every hook registered exactly once per load cycle
   - [ ] Records evicted from caches only after a confirmed save

4. **Thread-safety audit**
   - [ ] List every `Task.Run` / async continuation; confirm none dereferences
         `IPlayer`, `PlayerPawn`, controller or entity
   - [ ] Primitives captured BEFORE the hop (SteamID, slot, name)
   - [ ] Re-resolved and re-validated after `NextTick` / `NextWorldUpdate`
   - [ ] `ConcurrentDictionary` for shared state, not `Dictionary`

5. **Database audit**
   - [ ] Engine dispatch keys on `Driver`, not on the connection's CLR type
   - [ ] Every upserted table has the unique/PK its upsert depends on
   - [ ] Migrations verified on MySQL, PostgreSQL **and** SQLite, fresh and
         existing databases — not assumed from one engine passing
   - [ ] Every `IfDatabase(...)` string checked against a real
         `DatabaseTypeAliases` list — `"Postgres"` silently matches nothing
   - [ ] Every migration guards each object individually, not the migration as a
         whole — and `Create.PrimaryKey` is exempt from that guard
   - [ ] The version ledger, and every index and constraint name, carries the
         configured prefix/suffix
   - [ ] Each plugin migrates against **its own** ledger; nothing shares one
   - [ ] Resolved ledger names stay under 63 characters — PostgreSQL truncates
         silently, which can merge two modules' ledgers
   - [ ] Any Dommel table-name resolver filters on `type.Assembly` and is
         installed before the first query
   - [ ] No `Down()` drops a table holding player data
   - [ ] No identifier named `rank`
   - [ ] Connections and migration-runner providers disposed
   - [ ] Every table name routed through the affix resolver

6. **Dependency-direction audit** — specific to this plugin
   - [ ] No core type holds a delegate, interface or descriptor a module supplied
   - [ ] No core config key exists because a module is installed
   - [ ] Core's behaviour with zero modules present is identical to its
         behaviour with ten — if it is not, something inverted
   - [ ] `UseCoreRankingLogic = false` mutes **everything** downstream of a point
         value: handlers, gates, both multipliers, dynamic scaling
   - [ ] … and mutes none of: stat counters, ladder, scoreboard, commands,
         persistence, `AddPoints`, `SetPoints`
   - [ ] `GetPointSettings` returns live values in **both** modes, and has no
         setter
   - [ ] `AddPoints` is stored raw in both modes — no multiplier ever touches it

7. **Cross-plugin collision audit** — cannot be done from inside the folder
   - [ ] Every command name and alias greppable against sibling plugins
   - [ ] Shared-interface key and config filenames likewise
   - [ ] Short aliases (`top`, `stats`) are the usual offenders; first
         registration wins silently and the loser never fires

8. **Divergent-path audit**
   - [ ] Where two paths perform the same operation (chat command vs shared API,
         menu vs command), diff their validation and limits — the second path is
         where the cap, the permission check or the confirmation goes missing

9. **Failure-handling audit**
   - [ ] A failed load never substitutes an empty record — an absolute save
         would then wipe the real row
   - [ ] Every user-visible failure produces a localized message, not silence
   - [ ] User-controlled text validated before it reaches a format string or a
         colour-token pass

10. **Logging audit**
    - [ ] All `LogInformation`/`LogDebug` gated by `DetailedLogging`
    - [ ] `LogWarning`/`LogError` never gated
    - [ ] Structured placeholders, not interpolation
    - [ ] `LogError(ex, …)` passes the exception object
    - [ ] Nothing logs from a binder that re-runs per rebuild

11. **Translation audit** — mechanical, so do it mechanically
    - [ ] Extract every key referenced in code, every key in `en.jsonc`, every
          key in each other file; print the counts and the diffs
    - [ ] Every file parses as JSON
    - [ ] No hardcoded user-facing English in `SendChat` / catch blocks
    - [ ] Colour runs closed with `[default]`

12. **Performance audit**
    - [ ] No synchronous DB calls on the main thread
    - [ ] `OnTick` / `OnWorldUpdate` handlers allocation-free — no LINQ, no DB
          (and for this plugin: no subscription to either at all, NFR-11)
    - [ ] Hot-path player lookup is slot-indexed, not SteamID-hashed
    - [ ] Reflection results cached in a static `ConcurrentDictionary`
    - [ ] Save passes skip unchanged records
    - [ ] Config read from the swapped snapshot, not `CurrentValue` per access

13. **Comment audit** — mechanical, so do it mechanically
    - [ ] Grep `src/` for `//` and `/*` **inside method bodies**; the expected
          count is zero. Each hit is either extracted into a named method or
          promoted to `<remarks>` on the enclosing one
    - [ ] Every non-private method has a `<summary>`, and no `<summary>` runs
          past two lines
    - [ ] No `<param>` / `<returns>` that only restates the identifier
    - [ ] No commented-out code, no `TODO` without a tracking issue

### Common bugs

```csharp
// BUG: accessing a player inside Task.Run
Task.Run(async () => { var name = player.Name; });              // thread-unsafe
// FIX: capture first
var name = player.Name; var steamId = player.SteamID;
Task.Run(async () => await SaveAsync(steamId, name));

// BUG: no re-validation after a scheduler callback
Core.Scheduler.NextTick(() => player.SendChat("Hi"));           // may have left
// FIX
Core.Scheduler.NextTick(() => {
    if (player?.Controller is not { IsValid: true }) return;
    player.SendChat("Hi");
});

// BUG: Repeat* runs the first callback immediately, during Load()
Core.Scheduler.RepeatBySeconds(300, SavePass);                  // flushes mid-load
// FIX
Core.Scheduler.DelayAndRepeatBySeconds(300, 300, SavePass);

// BUG: non-idempotent registration in the handshake
public override void UseSharedInterface(IInterfaceManager im)
    => Core.GameEvent.HookPost<EventPlayerSpawn>(OnSpawn);      // one per rebuild
// FIX: hooks go in Load(); only idempotent registration here.

// BUG: binder early-returns and keeps a stale reference
if (!im.HasSharedInterface("Ranks.Api.v1")) return;             // _api kept
_api = im.GetSharedInterface<IRanksApi>("Ranks.Api.v1");
// FIX: TryGetSharedInterface nulls the out param on a miss, so one assignment fails closed
_ = im.TryGetSharedInterface<IRanksApi>("Ranks.Api.v1", out var api);
_api = api;                                                     // absence -> OnAllPluginsLoaded

// BUG: disconnect handler gated on IsValid — the pawn is already gone
if (!player.IsValid) return;                                    // save never runs
// FIX
if (player?.Controller is not { IsValid: true }) return;

// BUG: engine dispatch on CLR type — SQLite never matches
case SqliteConnection: rb.AddSQLite(); break;
// FIX
case "sqlite": rb.AddSQLite(); break;                           // from GetConnectionInfo().Driver

// BUG: a failed load creates an empty record, and the next absolute save wipes the row
var record = await LoadAsync(steamId) ?? new PlayerRecord(steamId);
// FIX: do not track the player; retry the load.
```

---

## 15. SwiftlyS2 Docs & CS2 Schema Dumps via @sd MCP

### Categories

| Category | Contents |
|----------|----------|
| `swiftlys2` | SwiftlyS2 API docs (classes, interfaces, methods, events, guides) |
| `counter-strike-2` | CS2 schema + binary string dumps (sub-category `CS2-Dumps`) |
| `source2` | FGD dumps, Source 2 docs, tooltex and console dumps |

### Usage

```
@sd list_documentation_categories
@sd browse_category("swiftlys2")              ← top-level category name ONLY
@sd search_documentation("ICommandContext")   ← single keyword or type name
@sd get_document("swiftlys2/docs-api-plugins-baseplugin.md")
```

- `browse_category` takes a **top-level name only** — `"category/subcategory"`
  is invalid input.
- `search_documentation` is **exact substring matching**. Use a single keyword
  or type name. Long natural-language phrases return nothing.
- **Never guess a document path.** Always take it from `browse_category` or
  `search_documentation`.

### Common lookups

| Need | Query |
|------|-------|
| Plugin base class | `BasePlugin` |
| Command context | `ICommandContext` |
| Shared interfaces | `IInterfaceManager` |
| Database service | `IDatabaseService` |
| Scheduler | `ISchedulerService` |
| Menus | `IMenuAPI` |
| Player interface | `IPlayer` |
| Framework events | `IEventSubscriber` |
| Game events | the event name, e.g. `EventPlayerDeath` |
| CS2 entity schemas | browse `counter-strike-2` → `CS2-Dumps` |

---

## 16. Build & Release

### Build

```powershell
dotnet build Ranks/Ranks.csproj -c Release
dotnet publish Ranks/Ranks.csproj -c Release
```

`Ranks.csproj` emits `build/publish/Ranks/` and zips it to
`build/Ranks-v<version>.zip` via the `CreateZip` target, which reads
`PluginMetadata.Version` out of `Ranks/src/Entrypoint.cs` with the `ExtractPluginVersion`
inline task. The version lives in exactly one place; nothing else needs stamping.

### Cutting a release

`.github/workflows/release.yml` runs on `workflow_dispatch`, or on a push to `main`
whose commit message contains `[release]`. It reads the same
`PluginMetadata.Version`, takes the matching `## [x.y.z]` section of the component's
`CHANGELOG.md` as the release body, and uploads the zip.

**One run publishes every component that has something to publish.** The `target`
input defaults to `all`: core and the four modules each get their own tag, notes and
release, from a single build. Core and the modules version independently, so under
`all` a component already published at its current version is **skipped with a
notice** rather than failing the run — otherwise bumping one module would be blocked
by the four that did not change. Naming a single component instead keeps the old
behaviour and still **errors** on an existing tag, which is what makes an accidental
re-release loud. A run where everything is already published is an error, not a
silent success.

Every component is validated before anything is built, so a version mismatch in one
cannot leave the others already released. A core release additionally attaches the
module zips built from the same commit, so an operator never has to work out which
module zip goes with which core version.

So a release needs **both** a version bump and a matching changelog section — a
missing section fails the run by design. Style rules live in
`.github/instructions/changelog.instructions.md`.

`docs/verify-release-workflow.sh` runs that logic against the real repo with `gh`
stubbed (`docker run --rm -v "${PWD}:/src:ro" debian:bookworm-slim`), which is the
only way to exercise it without publishing something.

### Install layout

```
addons/swiftlys2/plugins/Ranks/
  Ranks.dll
  resources/
    translations/
    exports/Ranks.Contract.dll
```

Configs are generated on first load into the plugin's config directory.

### Verifying the contract actually ships

The export is gitignored and build-generated, so the failure is CI-only and
silent. Simulate a clean checkout — deleting build state is not enough, the
gitignored artifact must go too:

```powershell
Remove-Item 'Ranks/resources/exports','Ranks/build','Ranks/obj' -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Ranks/Ranks.csproj -c Release
Test-Path 'Ranks/build/publish/Ranks/resources/exports/Ranks.Contract.dll'
```

Always run the control too — revert the packaging fix and confirm it **fails** —
otherwise you cannot tell the fix from a warm-cache effect.

### Release checklist

- [ ] Version bumped in `[PluginMetadata]` **and** `.csproj`
- [ ] `Ranks.Contract.dll` present in the publish output
- [ ] `en.jsonc` complete; contributed translations key-complete or omitted
- [ ] Migrations verified on MySQL, PostgreSQL and SQLite — fresh and existing
- [ ] Breaking contract change → key bumped to `.v2`, `.v1` still registered
- [ ] `CHANGELOG.md` has a `## [x.y.z]` section matching the new version

---

## Quick Reference: Repository Structure

```
Ranks/
├── .github/
│   ├── copilot-instructions.md          ← this file
│   ├── readme-instructions.md           ← README style
│   ├── instructions/
│   │   └── changelog.instructions.md    ← CHANGELOG.md style
│   └── workflows/
│       └── release.yml                  ← build + GitHub release
├── docs/
│   ├── functional-requirements.md       ← the specification
│   ├── implementation-guide.md          ← architecture
│   └── verify-wiki.ps1                  ← wiki checks (gitignored)
├── Ranks/                               ← core plugin
│   ├── Ranks.csproj
│   ├── src/
│   │   ├── Entrypoint.cs                ← plugin class, Load/Unload
│   │   ├── Api/                         ← IRanksApi implementation
│   │   ├── Commands/
│   │   ├── Config/                      ← config models + defaults
│   │   ├── Database/                    ← schema, migrations, store
│   │   ├── Logging/
│   │   ├── Menus/
│   │   ├── Players/                     ← record cache, load/save lifecycle
│   │   ├── Ranking/                     ← ladder resolution
│   │   ├── Scoreboard/                  ← rank icon, clan tag, overlay
│   │   └── Scoring/                     ← award pipeline, gates, multipliers
│   └── resources/
│       ├── examples/ranks-competitive.jsonc
│       ├── translations/en.jsonc        ← REQUIRED
│       └── exports/                     ← Ranks.Contract.dll (build-generated)
├── Ranks.Contract/                      ← public API assembly
│   └── IRanksApi.cs, RanksTypes.cs, RanksKillTypes.cs, RanksModuleBase.cs
├── Modules/
│   ├── Ranks_WeaponStats/               ← data, publishes its own contract
│   ├── Ranks_EloPoints/                 ← absolute, publishes its own contract
│   ├── Ranks_CustomCombos/              ← additive, publishes its own contract
│   └── Ranks_Prestige/                  ← display, no contract
├── CHANGELOG.md
├── LICENSE
└── README.md
```

Each module is its own project with its own `src/Api/RanksBridge.cs`, config
models, `CHANGELOG.md` and `README.md`. The consumer-facing wiki lives in the
separate `Ranks.wiki` repository.

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

Numbering is stable across the split, so a `§8` reference in the docs or in a code
comment still resolves. Gaps are sections that moved.

**Here, always loaded:**

1. [Plugin Structure & Base Variables](#1-plugin-structure--base-variables)
4. [Logging](#4-logging)
7. [Events, Listeners & Hooks](#7-events-listeners--hooks)
9. [Thread Safety & Async Data Access](#9-thread-safety--async-data-access)
10. [Data Management & Optimization](#10-data-management--optimization)
12. [Player Validation](#12-player-validation)
13. [Plugin Lifecycle](#13-plugin-lifecycle)
14. [Auditing & Bug Prevention](#14-auditing--bug-prevention) — **the invariants table**
15. [SwiftlyS2 Docs & CS2 Schema Dumps via @sd MCP](#15-swiftlys2-docs--cs2-schema-dumps-via-sd-mcp)

**In `.github/instructions/`, loaded only when you touch the matching files.** Read
the file directly when you need the rules without having one of those files open:

| § | Topic | File | Loads on |
|---|-------|------|----------|
| 2 | Configuration | `configuration.instructions.md` | `**/Config/*.cs`, `**/*.jsonc` |
| 3 | Database & migrations | `database.instructions.md` | `**/{Database,Migrations}/*.cs` |
| 5 | Commands | `commands.instructions.md` | `**/Commands/*.cs` |
| 6 | Translations | `translations.instructions.md` | `**/translations/*.jsonc` |
| 8 | Shared interfaces & the bridge | `shared-interfaces.instructions.md` | `**/Api/*.cs` |
| 11 | Menus & UI | `menus.instructions.md` | `**/Menus/*.cs` |
| 16 | Build & release | `release.instructions.md` | `.github/workflows/*.yml` |
| — | CHANGELOG style | `changelog.instructions.md` | `CHANGELOG.md` |
| — | README style | `readme.instructions.md` | `**/README.md` |

**Not instructions, and not loaded — reference material to open on demand:**

| Document | What it answers |
|----------|-----------------|
| [`docs/functional-requirements.md`](../docs/functional-requirements.md) | What the plugin must do, as numbered `FR-*` requirements, plus the open questions and the decisions behind them |
| [`docs/implementation-guide.md`](../docs/implementation-guide.md) | Why the architecture is shaped this way, the verified API surface, and the release checklist |
| `Modules/<name>/docs/` | Per-module features and implementation notes |
| `Ranks.wiki/` | Operator-facing docs; a separate repo, so changes there need their own commit |

Reach for `docs/` when changing behaviour, not when changing code: a `FR-*` id in a
comment or a commit is the link back to the requirement that made the code that way.

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
tick can still be too early. The one exception is the guarded probe that decides
whether a load lands on a running server (§13): a `try`/`catch` around the read
itself, answering "no map up" for the throw.

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
    <!-- No direct Microsoft.Extensions.* references: SwiftlyS2.CS2 brings them at the version SwiftlyS2
         itself builds against, and pinning them here fails restore (NU1605) whenever the SDK raises them. -->
    <PackageReference Include="SwiftlyS2.CS2" Version="1.4.13"
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

> `SwiftlyS2.CS2` must carry `ExcludeAssets="runtime" PrivateAssets="all"` — the
> host already provides it, and shipping a second copy produces duplicate type
> identities. `Microsoft.Extensions.*` is never referenced directly: `SwiftlyS2.CS2`
> brings it in, the exclusion carries over to it, and a pinned version fails
> restore with `NU1605` whenever SwiftlyS2 raises its own. The
> FluentMigrator packages are the opposite case: nothing provides them, so they
> must ship normally — beside the DLL, never in `resources/exports`, and without
> `PrivateAssets` (which silently drops them from publish). Modules ship their own
> copy too; why, and how core still runs their migrations, is in
> `instructions/database.instructions.md`.

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

- **`UseCoreRankingLogic = false` with no scoring claim** logs one ungated `Warning`
  at `OnAllPluginsLoaded`, after every module's handshake has had the chance to
  claim — or, after a load into a running server, which never reaches
  `OnAllPluginsLoaded`, a few seconds after `Load`. A claim granted over `true` logs one `Warning` naming the
  module, and a refused claim one `Error` naming both — each once, though the
  handshake that carries the claim re-runs per rebuild.
- A **scoring decision** (gate rejected, multiplier applied) is `Debug`.
- A **recoverable persistence failure** is `Warning`; anything that risks data
  loss is `Error`.
- Anything on the **award path** is gated behind `DetailedLogging` without
  exception — it runs at kill rate, and an ungated line there is a log flood.

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
List<ulong> players = [];
await Core.Scheduler.NextWorldUpdateAsync(() =>
{
    players = Core.PlayerManager.GetAllValidPlayers()
        .Where(p => !p.IsFakeClient)
        .Select(p => p.SteamID)
        .ToList();
});

foreach (var steamId in players)
    await SaveRecordAsync(steamId);
```

Return results through a captured local, not the value-returning `Func<T>`
overload. That overload works, but SwiftlyS2's load-time obsolete-API scanner
cannot tell it from the throwing `Func<Task<T>>` one and logs a false
`Plugin 'Ranks' uses obsolete API` warning.

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

// Loaded into a running server — adopt already-connected players. Not `if (hotReload)`:
// a load through SwiftlyS2's plugin API passes false on a running server (§13). On the game
// thread: the file watcher runs Load on a pool thread, and IsConnected reads the controller.
if (LoadsIntoRunningServer(hotReload))
    OnGameThread(AdoptConnectedPlayers);     // now when already there, else Scheduler.NextTick

void AdoptConnectedPlayers()
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
`OnSharedInterfaceInjected` fire many times per session, while `Load` fires once
per load of the plugin and `OnAllPluginsLoaded` only once, at server start. A hot
load or reload — `sw plugins load`, `sw plugins reload`, or the file watcher, which
runs the whole thing on a pool thread — calls `Load(true)` and rebuilds the
handshake, and never reaches `OnAllPluginsLoaded`. The watcher rebuilds only for a
DLL that changed: a new plugin it sees created gets `Load(true)` alone, and no
handshake until another load, reload or unload rebuilds it.

SwiftlyS2's plugin API — `IPluginManager.LoadPlugin` and `ReloadPlugin`, which reach
`LoadPluginById` — loads into the running server the same way, but passes
**`hotReload: false`**, the value a server start passes. So never branch on the flag
alone: "loading into a running server" is `hotReload || MapIsLoaded()`, where the
probe reads `Core.Engine.GlobalVars` inside a `try`/`catch`. SwiftlyS2 loads plugins
while it starts, before the first map, where that read throws; a later load normally
finds a map up. Core and every module that branches on it (adoption, migrations, the
delayed checks, the purge) decide it that way.

The exception is a plugin-API load landing mid-changelevel: `GlobalVars` is whatever
the engine's `GetServerGlobals()` returns, and nothing shows it stays readable between
maps. So "no map" means a server start only once `OnAllPluginsLoaded` confirms it —
SwiftlyS2 raises it inside its own start, so every real start gets it before its first
tick. Work only a start may do (core's forced purge) runs from there, and a load still
without it at its first tick switches on what a running server needs (core's background
migrations and both delayed scoring checks). Its players need nothing: every map load
puts each of them in server again. The modules' orphan sweeps follow core instead of
deciding: they run from the `MaintenanceDue` core raises after that forced purge, and
after every later pass, which only an empty server gets.

### Do NOT null-check shared-interface fields in `Load`

On the hot-reload path, `Load` runs before the framework has finished re-wiring
shared-interface fields, so every field reads null even when the provider is
loaded and healthy. At server start `OnAllPluginsLoaded` is the only place where
that state is authoritative; a check that must also run after a load into a running
server goes in a short `DelayBySeconds` from `Load`, as core's own scoring check does,
and `OnAllPluginsLoaded` cancels it when it does arrive after all.

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

        if (LoadsIntoRunningServer(hotReload))
            OnGameThread(AdoptConnectedPlayers);   // §12 — Load may run on a pool thread
    }

    public override void ConfigureSharedInterface(IInterfaceManager im)
        => im.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);

    public override void OnAllPluginsLoaded()
    {
        if (cfg.CurrentValue.General.DetailedLogging)
            logger.LogInformation("Ranks ready on connection {Connection}.", _api.ConnectionName);

        if (!cfg.CurrentValue.Rank.UseCoreRankingLogic && _api.ScoringOwner is null)
            logger.LogWarning("Core ranking logic is disabled and no module has claimed scoring.");
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
| `TableNames` | The plugin supplies the separator, not the operator. Empty affixes change nothing. A module ledger drops the `ranks` prefix. Overlong identifiers fold deterministically. Affixes are applied once, by `AffixingProcessor` — to a column's primary key name as well — so a ledger reaches `AffixedVersionTable` bare; `SchemaMigrator` renames a ledger that builds before 1.2.0 affixed twice before the runner reads it | A wrong affix writes to a real table that is simply the wrong one, a ledger collision reports migrations as applied that never ran, a ledger left under the double-affixed name reads as empty, so every migration runs again, and an unaffixed primary key name collides on PostgreSQL, where constraint names are schema-wide |
| Module migrations | `SchemaMigrator` queues a registration until `OnAllPluginsLoaded` drains the queue on its own thread; past that — and from the start of a core loaded into a running server, or from the first tick of one that took itself for a server start and saw no `OnAllPluginsLoaded`, which also moves the queue onto the chain — `Register` locates the module's file and hands it to one background chain, so it returns at once, never throws, and isolates each module's failure. Both sets and the chain sit behind one lock no background run holds; each `(assembly, pluginId)` migrates once per load of core, failed or not, and `Unload` drops the runs not yet started, then waits — bounded like the flush — for the one already running. Until a run finishes, a module's write to a table it creates fails, logged, and its retry buffer replays it with the next write — a reset or purge only until its own statements have failed three times for a reason the driver does not mark transient | SwiftlyS2 raises `OnAllPluginsLoaded` at server start only, so a queue nothing drains leaves a module loaded on a running server — or every module after a core reload — writing to tables that do not exist until the next restart; an exception out of `Register` fails every plugin on the server; a migration inside the handshake stalls the game thread for the length of a console load, and one under the lock stalls the next handshake; SwiftlyS2's plugin list is not safe to read from the background while another load changes it; every load of core gets its own load context, so nothing else stops a reload straight after a load migrating one module twice at once |
| `AwardPipeline.IsPlayCounted` | A gate stops points and statistics together, in core and in every module. Every counter write but playtime's sits behind it — directly, or through `ShouldRecordDeath` for deaths and hits — and it alone decides whether play counts: `MinPlayers` met, no warmup `WarmupPoints` excludes, and with `BotsEnabled` off no team made up only of bots. Decided once per server tick, so a kill's stat and its award, and every module's handler for the same event, see one answer. No module adds a gate of its own: WeaponStats counts and CustomCombos claims only while it is open, and EloPoints rates only rounds it counted — discarding a whole match on a restart, a map change or a season boundary withholds a rating, it does not gate play. A `round_end` naming neither T nor CT is nobody's win or loss | A counter written outside it records play the points refuse, which looks fine on any full server. Shipped that way: `MinPlayers` gated only awards, so deaths, kills and round results moved on an empty server, every drawn round cost both teams a loss, and EloPoints rated matches under a `MinPlayers` rule of its own |
| `AwardPipeline` scoring claim | While a module holds the claim, `Award` and `AwardClaim` both return before any gate, multiplier or `PointsChanging`, whatever `UseCoreRankingLogic` says; `AddPoints` and `SetPoints` are never gated by it; `GetPointSettings` keeps reporting the configured switch. Core holds the id string only. A grant over `true` warns, a refusal errors and a blank id warns, each once per holder or claimant. A module claims through the interface its handshake just bound, in its own `NoInlining` method, and releases it in `Unload`. EloPoints takes only `admin.set`, `admin.add` and `admin.subtract` into its rating — cache, session and an ordered write that counts no match, in one call — and every `IEloPointsApi` read skips a row with `matches = 0`, in history too, while its own resync and seeding do not. Core's `!setpoints` reports a total it left unchanged on `PointsChanged` with `Delta` 0; no other write reports one | A claim checked in one award path but not the other pays combos under a rating module; a claim made through a module's kept `Api` after a core reload leaves core scoring with no error; a log in the claim path repeats once per rebuild; an unreleased claim keeps core silent until it reloads; adopting any other reason lets a plugin's points outlive the rating, an adjustment written through the settle upsert counts a match nobody played, and an API read without the `matches` filter hands a player an admin rated before they played a standing — at the top of the leaderboard after a `!setpoints 2500`. An unreported unchanged `!setpoints` never reaches a rating another plugin's points have moved the total off |
| Scoring anchors | Equal players score the configured value; the difference is bounded and symmetric; the ladder picks the highest threshold met | A broken anchor makes every configured `kill` value mean something other than what it says |
| `ScoreboardService.EngineRankType` | Every visible mode maps to a **confirmed** engine rank type. `0` is the engine's "no rank" | CS2 renders nothing for an unrecognised `m_iCompetitiveRankType` and reports no error, so it looks like the feature is switched off. It shipped broken once, writing `(byte)mode` |
| `ScoreboardService.ResolveMode` | An unparseable or undefined mode falls back to the configured mode, never to `Off`. `Enum.TryParse` accepts numbers, so `Enum.IsDefined` is required | A module typo would otherwise blank every scoreboard on the server |
| `CustomIcon` | At the shipped defaults, ladder position 1 lands on icon 0 and saturates past 18; a margin larger than the position must clamp at 0. `CustomRankStep` scales positions into icon numbers, while Max and Margin stay positions, so a step of 1 is K4's mode 0 exactly; a result past `int` caps rather than wraps | The engine treats the value as an array index. A step applied to the wrong term still draws an icon — only the wrong one, and only once a server sets a step above 1 |
| `EloMath` | `Expected(r,r) == 0.5` and `Expected(a,b) + Expected(b,a) == 1`; zero evidence moves nothing; shrinkage is monotonic in `n`; the envelope clamp holds on a **corrupted** accrual; the sign floor cannot turn a win into a loss; the penalty is a flat deduction; ties share a percentile rank | A sign error is invisible on even teams, a missing clamp only shows on a lopsided match, and a wrong shrinkage weight just moves the ladder at the wrong speed. The clamp is unreachable on valid data — a 20 000-iteration random sweep passed with it deleted |
| EloPoints session rating (`MatchTracker`) | A settle moves every rated session onto its new rating after the whole loop, never inside it — Classic reads every teammate's rating through `TeamAverages`. Core's points seeded at join only stand in for the stored rating: a read landing for the same generation replaces the seed, never a rating a settle, an admin change or a reset has set since — a settle clears the seed, the other two bump the generation. A read finding no row resolves a session only once core holds the player's record; before that, `PlayerDataLoaded` seeds it from core's points, or StartPoints when those are 0. A read that fails is not one finding no row: core's points stand in on the same terms, and the read is retried three times, 5, 15 and then 45 seconds apart, each time only for the same generation and while a stored rating would still be taken | A rating left unmoved rates a player who stays from the rating they joined with, so every settle overwrites the last; a rating moved mid-loop scores later players against teammates already moved; a seed the read cannot replace drops the rating's fraction and carries points another plugin gave into the next settle; an empty read resolved before core loaded the player reads their points as 0, so someone moving over from core scoring starts at StartPoints; a failed read taken for an empty one starts the player from their rounded points for good, and the next settle writes that over the rating the read never reached. Every one of them is a plausible number |
| EloPoints sessions (`MatchTracker.Adopt`) | A player gets a session at put-in-server, or — when that found no SteamID or no Ranks bound — from core's `PlayerDataLoaded`. After a load into a running server, the first handshake that binds Ranks adopts everyone already connected, on the game thread, taking each one's match-start counters from core there, and voids a match past its first round; a later rebind after a core reload adopts nobody. A new session's counters come from `PlayerDataLoaded` or that adoption; past that only a re-baseline — a new match, a new period, a reset — sets them | Nothing else ever adds a session, so a player Steam authorizes late, or one who entered while Ranks was not loaded, is never rated, and a reload adopting from `Load` — before Ranks is bound — rates nobody until they reconnect; a rebind that adopted again would void the match being rated. Adopted without counters — their `PlayerDataLoaded` is long past — every player scores at the median: nobody earns a performance bonus, and a later joiner is ranked against a row of zeros. Each looks like players who simply did not play, or played evenly |
| WeaponStats sessions (`Collector.Adopt`) | A session opens at put-in-server, or for everyone connected after a load into a running server, only for a SteamID other than 0. Under SwiftlyS2's strict Steam authorization a player reads 0 until Steam confirms them, so theirs opens from core's `PlayerDataLoaded` instead, for the connected non-bot holding that SteamID. Both of those run on the game thread — the load's a tick later when `Load` is off it — and never open over a session the slot already has. Play before that goes unrecorded, as in core | A session opened on 0 files every shot, hit and kill under `steam_id` 0 — one row shared by everyone Steam confirmed late, owned by nobody — while their own rows stay empty. A replaced session drops the deltas it had not flushed, which core's `PlayerDataLoaded` on every reconnect and core reload would do, and so would a load's adoption racing the game thread from the file watcher's pool thread, where a player leaving mid-loop also throws out of `Load`. The orphan sweep removes the 0 rows, since core never keeps one |
| `PrestigeLadder` | Thresholds resolve to the highest level reached; crossing one resets the display to zero; operator order and duplicates do not change the outcome | A wrong level silently shows the wrong number to the player |
| `ComboMatcher` | Closest match wins, ties go to config order, `RequiresNone` excludes, a count combo needs the whole run and keeps accumulating after a loss, weapon class accepts both spellings | A combo that never fires looks like the operator configured it wrong |
| Weapon names | Both event spellings land on one row; non-weapon sources keep their bare name; unusable values become `unknown` | Stats split silently across two rows for the same gun |
| `MenuEntryRegistry` | A module's entries die with it, and re-registering the same `(moduleId, Section, Label)` replaces rather than appends | The handshake re-runs on every other plugin's load, so an append shows the row once per reload; an entry left behind is a delegate into an unloaded assembly load context, which crashes only when someone opens that menu |
| Menu nesting | Every parent-child link is a `SubmenuComponent`, never `ButtonComponent` + `Open()` | `Open()` starts a new root with no back-stack. The menu looks right until the player presses back and the whole tree closes |
| `PlayerRecord.ClearProgress` | Zeroes **every** counter, including `Headshots`, `Hits`, `Misses` and `Playtime`, and is the only code that does — the reset and the period rollover both go through it | A forgotten column leaves a "reset" player carrying their old accuracy, which only shows up on a stat nobody checks first. The command's copy already had this bug before the reset paths were merged |
| Playtime clock | Seconds connected, counted as K4-LevelRanks counted them and never gated by `IsPlayCounted`. Banked on the game thread only — by a save called from it, by `StartPeriod` before the archive is cut, and at disconnect, which also stops it. Started only for a player still connected when their load publishes, and never restarted on a clock already running | A clock started for someone who already left banks time forever, since no disconnect follows; a rollover that does not bank first moves a season's last minutes into the next; banking off the game thread races the disconnect that stops the clock; a restart on the second connect trigger drops the seconds since the first. Each one only shows as a playtime figure nobody can check |
| Period rollover (FR-DB-10) | A resident record's closed-period figures are written to `ranks_players_history` in the **same savepoint** as its fresh row. `PendingArchives` is replaced, never mutated, and cleared only by compare-and-swap. Period ids only rise: boot and rollover both go through `PeriodClock.Due`, which holds the active period until `PeriodClock.EndOf` and only then moves to the grid period of that moment. Under Monthly every `EndOf` is a 1st — a period not starting on the 1st began under Days and ends at the first 1st on or after its N-day end. Only a stored period **older** than the active one is closed; a newer one moves the server to it (`PlayerSessions.CatchUp`), whatever its own schedule says. Nothing guesses the period: if it cannot be read at boot, nothing is recorded | Every failure here looks like a quiet season: a lost archive is a past leaderboard missing a player, a plain clear in `Settle` (which runs a tick after the write) drops a period a rollover queued between the snapshot and the settle, a smaller id after a schedule change makes every older-or-newer comparison — core's and EloPoints' — archive the live season backwards, closing a Days leftover at the current month's 1st resets everyone mid-month under a season named for the whole month, and moving a newer record back to the server's period archives that player's live season early. None of them errors |
| Player saves (`PlayerSessions`) | Every player-row write — a batch pass, one player's save, the unload flush — runs through one ordered queue, snapshotted under its lock, and stamps the record with that write's number there. Settling — the dirty compare, `SettleArchives`, `PlayerDataSaved`, eviction — is posted to the game thread after the commit and never awaited, and the unload pass settles nothing. Only the record's newest queued write may clear its dirty flag or evict it; an older one settles archives and raises `PlayerDataSaved` only. `SaveAllAsync` joins the pending pass; `FlushOnUnload` queues one of its own | Two connections commit in either order, so a batch snapshot landing after a newer disconnect save puts stale values back over a record already settled clean and evicted — the player's last progress is gone, with no error. A settle off the game thread raises `PlayerDataSaved` where the contract says it never is; a write that awaits the tick holds `FlushOnUnload`'s blocked game thread for its whole budget; an unload that joins a pass in flight drops everything since that pass's snapshot. An older write's settle can find a record that moved and came back on that write's values, and clearing it there leaves the row on the newer write's values, with nothing left to rewrite them |
| Record residency (`PlayerSessions`, `PlayerRegistry`) | A load publishes onto the slot its owner holds when it lands, or onto none once they have left. Every settle of a record's newest queued write drops it when it is clean and no connected player is bound to it — whichever write that is — and only while the registry still maps that SteamID to that same record | Bound to the slot captured when the load began, a record is credited with whatever that slot's next occupant does until their own load lands. A clean departed record kept resident waits for its owner, who adopts it over a row another server sharing the database may have moved since. Evicting by SteamID alone drops the newer record a returning player loaded meanwhile |
| API writes (`RanksApi`) | `AddPoints`, `SetPoints`, `SetDisplayOverride` and `ClearDisplayOverride` run on the game thread: at once there, otherwise on the next tick — an `IPlayer` overload only while its slot still holds that SteamID — so the events they raise are game-thread too | A module's background task writing a record, a controller or the scoreboard races the game thread, and a disposed `IPlayer` throws on the tick. Either is a rare corruption or crash with no stack pointing at the caller |
| Loading into a running server | Decided as `hotReload` or a map being up, never the flag alone, by core and by every module that branches on it: adopting players, the rank reveal, migrating on registration, the delayed checks, no purge. Core adopts the connected players and re-sends the reveal, and WeaponStats adopts them, on the game thread — inline from a console load, on the next tick from any other thread. The map probe reads `GlobalVars` inside a `try`/`catch`. Whether `GlobalVars` stays readable between maps cannot be established — SwiftlyS2 returns the engine's `GetServerGlobals()` as is — so "no map" is a start only once `OnAllPluginsLoaded` confirms it, which every real start raises before its first tick: core's forced maintenance pass runs from it, and a load still without it at its first tick switches to the running-server path — core migrating on registration, its queue drained first, and core's and CustomCombos' delayed scoring checks, which `OnAllPluginsLoaded` cancels when it comes after all. Players re-enter at every map load, so adoption needs nothing. The modules' orphan sweeps decide nothing themselves: each runs once per load of its module, from the `MaintenanceDue` core raises after that forced pass and otherwise only on an empty server | `IPluginManager.LoadPlugin` and `ReloadPlugin` pass `hotReload` false on a running server, so a flag-only branch purges with players connected, adopts nobody, and leaves later registrations unmigrated — every one of which looks like a fresh start. A plugin-API load landing mid-changelevel could fail the probe the same way, and without the first tick its modules' tables would wait for a restart. The file watcher runs `Load` on a pool thread, where tracking reads each player's name and the reveal sends a user message — a rare crash with no stack pointing at the reload. A sweep started from a bind or a ready check deletes rows with players connected |
| Module period stamping | A module stamps counts it was still holding with `PeriodChanged`'s `OldPeriod`, never `CurrentPeriod`, which has already moved — and only while `ArePeriodsEnabled`, since core's catch-up raises `PeriodChanged` with periods off too; a delta's period is fixed when its batch is cut and a retried write keeps it; every database write a module makes — flush, reset, purge — runs through one ordered queue, and one that fails waits in the retry buffer in that same order, a reset or purge like any flush, with the buffered deltas a reset covers dropped first; a reset or purge whose own statements have failed three times is dropped with one `Error` and what was queued behind it written at once, while a failure the driver marks transient (`DbException.IsTransient`, on the exception or any it wraps), or one to open, begin or commit, or in another change, never counts against it; a module holding an authoritative per-player value drops it on `PeriodChanged` and `PlayerReset` | Stamping with the new period moves the last minutes of a season into the next; stamping with periods off writes per-period rows on a server that keeps none; a reset that bypasses the queue is overtaken by a flush cut before it and the cleared figures come back; a failed reset dropped at its first failure leaves the figures `!resetrank` said it cleared, and one retried behind deltas cut after it clears those too; one the database always refuses — no `DELETE` permission, a period table never created — kept for good holds every later write back for the rest of the load, while counting an outage toward the three would drop one it applies once back — and Npgsql sends `BEGIN` with the change's first statement, so a pooled connection that died idle fails there; a kept rating is written straight back over core's reset at the next match end, which is the `!resetrank` bug EloPoints shipped with |
| Renderer menu ids | `ranks.top`, `weaponstats.weapons` and `customcombos.*` may be reused by every period variant — a renderer reads their figures off the rows. `ranks.top` is the XP board only; the playtime board is `ranks.top.playtime`, because its rows carry seconds. `ranks.stats.main` and `ranks.top.player` stay the current period's, and `weaponstats.hitgroups` / `weaponstats.weaponhitgroups` stay all-time, because a renderer fetches those figures itself | CustomHUD draws its own numbers over a page it recognises by id, so a reused id shows this season's or all-time figures under a past season's title — on a page that looks perfectly designed. Under `ranks.top` it would draw playtime seconds as XP, with a rank icon resolved from them |
| Accuracy | `Hits / (Hits + Misses)`, never `Hits / Shots` | One shotgun blast is a single trigger pull that lands several hits, so a shots-based figure quietly exceeds 100% |
| `PagedCollectionComponent<TItem>` | `TItem` is the **row type**, and the text comes from `Formatter` | The default formatter is `ToString()`, so passing `IMenuComponent` items compiles, runs, pages correctly — and draws `SwiftlyS2.Shared.Menu.Components.TextComponent` on every line. Shipped that way in the leaderboard and all three module menus |
| Menu row width | A row is one centred line and clips at roughly **42 characters** | Both ends are cut off with no ellipsis, so a too-long row looks like corrupted text rather than a layout problem. Sentences go to chat |
| `IMenu.Parent` | A menu opened from a **row handler** must have `Parent` set by hand | Adoption only happens for a menu a `SubmenuComponent` opened. From `OnItemSelected` the child opens fine and looks right — the break only shows when the player presses back and the whole tree closes |
| Bridge calls from event paths | Anything reachable from a `[GameEventHandler]`, a `Registrator` registration, a scheduler callback or a config-reload callback must check `_bridge.IsAvailable` before reaching a bridge member that names a contract type | `IsAvailable` is a plain bool and resolves nothing, while a member that names a contract type fails to JIT with no Ranks installed and throws into the *caller's* frame — a game-event, `NextTick` or reload-callback body, where nothing catches it. WeaponStats avoids this structurally by keeping its collector contract-free — it reaches core's play gate only through a delegate whose target checks `IsAvailable` first; a module whose tracker calls the bridge needs the guard instead. Prestige's `UpdateLadder` and CustomCombos' `UpdateRules` name no contract type and check `IsAvailable` themselves before the `NoInlining` member that does, so their reload callbacks call them unguarded: each stores what it is handed first, and a guard at the call site would lose an edit made while Ranks is not bound |
| Module rebinding (bridges' `Attach`) | A module object serves the `IRanksApi` it was built for. When a handshake hands over a different instance — core reloaded — the bridge unregisters the old module inside a `try`/`catch` and builds a new one, whose `Register` replays `OnCoreReady`. Everything a module drives lives in the plugin, and every `OnCoreReady` is safe to replay: writers re-configure, menu entries re-register, a one-shot log fires at most once per core instance — or per load of the module, flagged in the plugin, as CustomCombos' `SuppressCoreAwards` note and EloPoints' adoption of connected players are — and Prestige's overlay passes, on a rebind and after a config reload alike, run on the game thread with each player guarded on their own, and one queued for a later tick is skipped once the bridge no longer serves the module that queued it | A module kept across a core reload stays subscribed to the unloaded core's cleared hub: `PlayerDataLoaded`, `PointsChanged`, `PeriodChanged` and `PlayerReset` stop arriving, its menu entries are gone and admin changes go unadopted, with nothing in the log. The file watcher runs that handshake, and a config reload, on a pool thread, where an overlay write reaches a controller; a player disconnecting mid-pass throws on every read, which unguarded leaves everyone after them on the old overlay. A queued pass that outlives its module writes through a core that has unloaded, or puts back the overlays an Unload has just cleared, with nothing left to clear them |
| Module idle warning | Report absence from `OnAllPluginsLoaded`, never from the `UseSharedInterface` / `OnSharedInterfaceInjected` path. `OnAllPluginsLoaded` runs at server start only, so a check that must also follow a load into a running server — core's and CustomCombos' scoring checks — is repeated from a short delay after `Load`, and `OnAllPluginsLoaded` cancels that delayed one before running its own | That handshake re-runs on every plugin load and unload, so warning there repeats once per rebuild rather than once at startup; a check left only in `OnAllPluginsLoaded` never runs after a load into a running server; SwiftlyS2 itself loading mid-game reaches both, and logs the warning twice |
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
         behaviour with ten, apart from what a module asked for through the API —
         a scoring claim, a kill arbiter, a menu entry. If merely installing a
         module changes anything, something inverted
   - [ ] `UseCoreRankingLogic = false`, or a module's scoring claim, mutes
         **everything** downstream of a point value: handlers, gates, both
         multipliers, dynamic scaling
   - [ ] … and mutes none of: stat counters, ladder, scoreboard, commands,
         persistence, `AddPoints`, `SetPoints`
   - [ ] `GetPointSettings` returns live values in **both** modes, reports the
         configured switch rather than the claim, and has no setter
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

## Quick Reference: Repository Structure

```
Ranks/
├── AGENTS.md                             ← entry point for tools that do not read .github/
├── .github/
│   ├── copilot-instructions.md          ← this file, always loaded
│   ├── instructions/                    ← loaded only when applyTo matches
│   │   ├── changelog.instructions.md        CHANGELOG.md
│   │   ├── commands.instructions.md         **/Commands/*.cs
│   │   ├── configuration.instructions.md    **/Config/*.cs, **/*.jsonc
│   │   ├── database.instructions.md         **/{Database,Migrations}/*.cs
│   │   ├── menus.instructions.md            **/Menus/*.cs
│   │   ├── readme.instructions.md           **/README.md
│   │   ├── release.instructions.md          .github/workflows/*.yml
│   │   ├── shared-interfaces.instructions.md **/Api/*.cs
│   │   └── translations.instructions.md     **/translations/*.jsonc
│   └── workflows/
│       └── release.yml                  ← build + GitHub release
├── docs/
│   ├── functional-requirements.md       ← the specification
│   ├── implementation-guide.md          ← architecture
│   ├── verify-release-workflow.sh       ← release-workflow harness
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
│   │   ├── Players/                     ← record cache, load/save lifecycle, period rollover
│   │   ├── Ranking/                     ← ladder resolution
│   │   ├── Scoreboard/                  ← rank icon, clan tag, overlay, reveal-all
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
│   └── Ranks_Prestige/                  ← display, publishes its own contract
├── CHANGELOG.md
├── LICENSE
└── README.md
```
Each module is its own project with its own `src/Api/RanksBridge.cs`, config
models, `CHANGELOG.md` and `README.md`. The consumer-facing wiki lives in the
separate `Ranks.wiki` repository.

# Ranks_EloPoints — implementation

> **Status:** design, implemented. The restart-reset question is closed
> (confirmed on a live server, §5.3); remaining open items are in §10 and none
> of them block.
> Companion to [features.md](features.md); core conventions it inherits live in
> the [implementation guide](../../../docs/implementation-guide.md).

Where `Ranks_WeaponStats` is the floor case for a **table-owning** module, this
is the floor case for an **absolute scoring** module: it replaces core's scoring
entirely, reads core's counters as inputs, and writes the only number that
reaches the ladder. If the shape below is awkward, the FR-PTS-00 contract is
wrong — not this module.

---

## 1. Layout

```
Modules/Ranks_EloPoints/
├── README.md
├── docs/
│   ├── features.md
│   └── implementation.md
├── Ranks_EloPoints.csproj
├── resources/
│   ├── templates/config.jsonc
│   ├── translations/en.jsonc
│   └── examples/ranks-elo.jsonc     # 10-band ladder for core's config
└── src/
    ├── Ranks_EloPoints.cs           # BasePlugin, lifecycle, contract isolation
    ├── Api/RanksBridge.cs           # the ONLY file naming IRanksApi
    ├── Api/CoreTypes.cs             # CoreSettings, the flattened view of core's settings
    ├── Config/Config.cs
    ├── Migrations/M202610261200_Rating.cs
    ├── Migrations/M202611060100_Periods.cs  # period column + ranks_elopoints_history
    ├── Match/MatchTracker.cs        # game-event handlers, match state machine
    ├── Match/MatchSession.cs        # one player's accrual for one match
    ├── Rating/EloMath.cs            # pure, static, no engine types
    ├── Rating/Performance.cs        # PerfCounters, weights, percentile
    └── Rating/RatingStore.cs        # off-thread reads and writes
```

`Ranks_EloPoints` — underscore, in the folder, the project and the
`[PluginMetadata] Id`. Core derives the migration ledger from that id.

**`EloMath` names no SwiftlyS2 type and no contract type.** Everything it needs
arrives as `double`, `int` and arrays, so the formula can be read and reasoned
about without the framework in the way.

---

## 2. Lifecycle

### 2.1 ⚠️ Contract isolation comes first

Identical to `Ranks_WeaponStats`, and for the same reason: per implementation
guide §10.2 the CLR resolves every type a method *names* when it JITs that
method, so on a box without `Ranks` a field typed `IRanksApi?` throws
`FileNotFoundException` at the **caller's** frame, and SwiftlyS2 reports
`Hosting failed to start` for every plugin installed.

Exactly one file — `Api/RanksBridge.cs` — names a contract type, every member
touching one is `[MethodImpl(MethodImplOptions.NoInlining)]`, and the field is
`object?`:

```csharp
internal sealed class RanksBridge
{
    private object? _api;                       // deliberately not IRanksApi
    private object? _module;
    public bool IsAvailable { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager) { /* ... */ }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire() { }
}
```

This module needs **more** through the bridge than WeaponStats does, and each
accessor is a separate `NoInlining` member returning a primitive or a plain
record:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
public int? GetPoints(ulong steamId) => _api is IRanksApi a ? a.GetPoints(steamId) : null;

[MethodImpl(MethodImplOptions.NoInlining)]
public void SetPoints(ulong steamId, int points, string reason) =>
    (_api as IRanksApi)?.SetPoints(steamId, points, reason);

// Flattened deliberately: returning RanksPlayerSnapshot would name a contract
// type in the caller's signature and defeat the isolation.
[MethodImpl(MethodImplOptions.NoInlining)]
public PerfCounters? GetCounters(ulong steamId)
{
    if (_api is not IRanksApi a || a.GetSnapshot(steamId) is not { } s) return null;
    return new PerfCounters(s.Kills, s.Deaths, s.Assists, s.RoundWins, s.BombsPlanted, s.BombsDefused);
}

[MethodImpl(MethodImplOptions.NoInlining)]
public CoreSettings? GetSettings() =>
    _api is IRanksApi a && a.GetPointSettings() is { } p
        ? new CoreSettings(p.UseCoreRankingLogic, p.StartPoints, p.MinPoints, p.MinPlayers, p.WarmupPoints, p.BotsEnabled)
        : null;
```

`PerfCounters` and `CoreSettings` are module-owned records. **Returning
`RanksPlayerSnapshot` or `RanksPointSettings` directly would put a contract type
in a signature outside the bridge**, which is the same JIT hazard one indirection
further out — and it is the exact mistake the `object?` field is there to
prevent.

**Audit rule:** grep the *field name* (`_api`, `_module`) across the whole
plugin, not the type name.

**The period is the one accessor that is not `NoInlining`.** `CurrentPeriod()`
names no contract type itself: it checks `IsAvailable`, then calls the
`NoInlining` `ReadCurrentPeriod()`. So the tracker, the published API and every
event path call it without a guard of their own, and it answers `null` while core
is unavailable. Against a Ranks older than 1.1.0 the inner read throws
`MissingMethodException`, which surfaces in `CurrentPeriod()`'s frame: it logs one
warning and answers `0` from then on. Every row of such an install is on period 0,
so the module behaves exactly as 1.0.1 did. Checked against the 1.0.4 contract:
`GetTypes()` still loads `EloPointsModule` — its `OnPeriodChanged` and
`OnPlayerReset` overrides simply have no base slot to fill — and the handshake,
`OnCoreReady` and `PlayerDataLoaded` all run.

### 2.2 Where things go

| Step | Work |
|---|---|
| Constructor | nothing |
| `Load` | config, `Core.Registrator.Register(_tracker)`, core-event subscriptions |
| `UseSharedInterface` / `OnSharedInterfaceInjected` | acquire through the bridge; claim scoring (§2.4); `RegisterMigrations(assembly)` |
| `OnCoreReady` | resolve both table names; report a refused claim, or — against a Ranks older than 1.5.0 — run the `UseCoreRankingLogic` check (§2.4); after a load into a running server, adopt the players already connected, once (§2.5) |
| `OnPointsChanged` | take an admin's change into the rating (§8.4) |
| `OnPeriodChanged` | drop the rating cache, leave a match the boundary cut through unrated, re-seed every session (§8.2) |
| `OnPlayerReset` | queue the player's rating delete, re-seed their session (§8.3) |
| `OnAllPluginsLoaded` | if `Ranks` is absent, log one ungated warning and stay idle |
| `Unload` | discard any open match, release the claim, then unwire |

`RegisterMigrations` goes in the handshake, not `Load` — there is no API in
`Load` (guide §10.5). Core dedupes on `(assembly, pluginId)`, so the handshake
re-running on every other plugin's load is harmless.

Table names are resolved in `OnCoreReady` for the same reason.

### 2.3 Handlers outside the plugin class must be registered

`MatchTracker` holds the `[GameEventHandler]` methods, so it is invisible to the
framework until:

```csharp
Core.Registrator.Register(_tracker);   // exactly once
```

Registering twice double-counts every round. Signatures must match the expected
delegate exactly or the method is silently skipped.

### 2.3a ⚠️ No native access inside an event dispatch

**Never touch native entity accessors synchronously from inside a game-event
handler, even in `Post` mode.** CS2 can be mid entity-shuffle, and a managed
`try/catch` does **not** catch the resulting access violation — it bypasses the
CLR straight to the crash reporter, taking the host down.

So `OnRoundEnd` reads only `e.Winner` and defers; `OnWinPanelMatch` defers
outright:

```csharp
public HookResult OnRoundEnd(EventRoundEnd e)
{
    var winner = (int)e.Winner;
    _core.Scheduler.NextTick(() => AccrueRound(winner));
    return HookResult.Continue;
}
```

`OnPlayerTeam` is the deliberate exception: its only native read is the named
player's own SteamID, whose pointer is stable for the event that names them, and
deferring risks them being gone by the next tick.

Two further rules follow from the same hazard:

- Guard with `player.IsValid` **and** `player.Controller is { IsValid: true }`.
  A non-null controller wrapper can sit over a dead pointer, and reading a field
  through it faults.
- The match-end watchdog uses `DelayAndRepeatBySeconds(5, 5, …)`, not
  `RepeatBySeconds`. The latter fires its first callback on the *next* scheduler
  tick, which lands inside CS2's map-load shuffle — and that callback reads game
  state.

### 2.4 Claiming scoring

The module takes scoring from core (FR-API-14) rather than asking the operator to
switch it off, so an install that never touched `Rank.UseCoreRankingLogic` still
gets exactly one authority:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private void Attach(IRanksApi api)
{
    ClaimScoring();                 // first, so OnCoreReady (raised inside Register) knows the outcome

    var module = ModuleFor(api);    // the module built for this API, rebuilt after a core reload

    module.Register();
    module.HandOverMigrations(typeof(RanksBridge).Assembly);
}
```

- **Every handshake claims again.** The claim is idempotent, and a core reload
  builds an API that holds none. It goes through `_api`, the interface this
  handshake bound — never the module's `Api`, which still points at the unloaded
  core until `ModuleFor` rebuilds the module.
- **A core reload rebuilds the module.** `ModuleFor` keeps the module while the
  handshake hands over the instance it was built for. Handed another one, it
  unregisters the old module inside a `try`/`catch` — that core is gone — and
  builds a new one, whose `Register` subscribes to the new core and replays
  `OnCoreReady`. The tracker and the store live in the plugin, so no session or
  rating is lost.
- **Against a Ranks older than 1.5.0** the claim is a `MissingMethodException` from
  its own `NoInlining` helper, caught in `ClaimScoring`; the state becomes
  `Unsupported` and stays so, since the contract cannot change without a restart.
- **`Unload` releases it**, from a separate `NoInlining` helper reached only after a
  granted claim — inline, an old contract would fail `Unwire` at JIT before the
  `Unregister` that follows.
- **Logging is once per core instance**, from `OnCoreReady`, never from the claim:

| Claim | Logged by the module |
|---|---|
| held | nothing — core logs the claim itself when it overrides `UseCoreRankingLogic = true` |
| refused | one `Error` naming the module that holds it |
| unsupported | the old check: one `Warning` when `UseCoreRankingLogic` is `true`, adding that Ranks 1.5.0 turns core's scoring off by itself |

All ungated by `DetailedLogging` — each fires once, and is the only signal between
a misconfigured server and a ladder full of nonsense. Against an old Ranks the
module still warns rather than overriding: that core has no claim to take, and
its config belongs to the operator.

### 2.5 Loading into a running server

A reload — `sw plugins reload`, the file watcher, or SwiftlyS2's plugin API, which
passes `hotReload: false`, so the module decides "running server" as
`hotReload` or a map being up — finds players connected and, often, a match under
way. `Load` cannot adopt them: a session needs Ranks, and no load path has bound
it yet. So `Load` only sets a flag, and the first `OnCoreReady` after it posts
`MatchTracker.AdoptConnectedPlayers` to the next tick — on the game thread, since
the file watcher runs that handshake on a pool thread:

- **Everyone connected gets a session**, resolved like a joiner's (§4.3). Core
  already holds their records, so an empty read seeds from core's points at once,
  and their match-start counters are read from core there and then: their
  `PlayerDataLoaded` fired before the module bound, and a session without counters
  scores at the median (§7.2) — every adopted player would then miss the
  performance bonus, and a later joiner be ranked against them. A record core is
  still loading fills both from its `PlayerDataLoaded` instead.
- **A match past its first round stays unrated.** Its earlier rounds were never
  accrued, and reconstructing them from the scoreline would mean inventing the
  per-round expectations. The match is voided like one a season boundary cuts
  through (§8.2), with the round count taken as the restart baseline so an
  `mp_restartgame` straight after still starts a rated match. The next match is
  rated normally.
- **Once per load of this module.** A Ranks reload replays `OnCoreReady` on the
  rebuilt module (§2.4); adopting again there would void the match the module is
  already rating.

That is the same rule as a map change (features.md §2.1), reached a different
way. A plugin-API load landing between maps may find no map and adopt nobody on
bind; nothing is lost, since the players are put in server again as that map loads
and the match there is new.

---

## 3. Configuration

```csharp
Core.Configuration
    .InitializeJsonWithModel<EloConfig>("config.jsonc", "EloPoints", true)
    .Configure(builder =>
        builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true));

_config = BindConfig();

_reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
{
    var incoming = BindConfig();

    _config = incoming;
    _tracker?.UpdateConfig(incoming);
});
```

Bound off `Core.Configuration.Manager` — the root the framework already owns and
watches. No `ServiceCollection`, no `IOptionsMonitor` (guide §3).

Validation clamps rather than throwing:

| Key | Rule |
|---|---|
| `Rating.Mode` | `Classic` or `RoundAccumulation`; anything else falls back to `RoundAccumulation` with a warning |
| `Rating.K` | `1..200` |
| `Rating.PerformanceBonus` | `0..100`; `0` disables the bonus |
| `Rating.ConfidenceRounds` | `0..100`; `0` disables shrinkage |
| `Rating.ExpectedRounds` | `1..200` |
| `Abandon.Penalty` | `>= 0`; `0` disables |

**`Mode` falls back loudly.** An unrecognised string binding to
`default(RatingMode)` would silently pick whichever member is zero, and the
operator's typo would look like working software.

### 3.1 Settings read from core, not configured here

`StartPoints` and `MinPoints` come from `GetPointSettings()` on every match settle,
never cached at load — `GetPointSettings` returns a fresh snapshot per call precisely
so it tracks live reloads (FR-API-05). `MinPlayers`, `WarmupPoints` and `BotsEnabled`
reach the rating through core's play gate instead (§5.2), which core applies to its
statistics in every mode; the snapshot copies are read only against a Ranks older than
1.0.4, which has no gate to ask.

This is FR-MOD-05 rules 3 and 7: the operator states each intent once, in core, and the
rating moves on exactly the play core's statistics count.

---

## 4. Database

### 4.1 Migrations

```csharp
[Migration(202610261200)]
public sealed class M202610261200_Rating : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ranks_elopoints").Exists())
            Create.Table("ranks_elopoints")
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();
    }

    public override void Down() { }   // the table holds player data
}

[Migration(202611060100)]
public sealed class M202611060100_Periods : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ranks_elopoints").Column("period").Exists())
            Alter.Table("ranks_elopoints").AddColumn("period").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table("ranks_elopoints_history").Exists())
            Create.Table("ranks_elopoints_history")
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();

        if (!Schema.Table("ranks_elopoints_history").Index("ix_ranks_elopoints_history_period").Exists())
            Create.Index("ix_ranks_elopoints_history_period").OnTable("ranks_elopoints_history")
                .OnColumn("period").Ascending();
    }

    public override void Down() { /* drops the index only */ }
}
```

The same four rules as WeaponStats §4.1 apply: the base name carries the
`ranks_` namespace so FR-DB-03's resolver only adds the operator's affixes; the
PK is declared inline because MySQL names every primary key `PRIMARY` and a
`Constraint(...).Exists()` guard is therefore always false; migration ids are
unique within this assembly only, since the module has its own ledger; and no
`Execute.Sql`, which has no expression tree for core to rewrite. Every object is
guarded on its own, so a half-applied run finishes on the next boot.

**The `period` column defaults to 0**, `RanksPeriods.Initial` — core's current
period on every install that never enabled periods — so existing ratings stay
current and nothing changes until periods are switched on. `ranks_elopoints`
keeps `steam_id` as its only key, as core keeps it on `ranks_players`: SQLite
cannot alter a primary key in place, so closed periods get a table of their own
rather than a wider key on this one. The history index serves the past-season
leaderboard, which filters on `period` alone and so cannot use the
`(steam_id, period)` key.

**`DECIMAL(10,4)`, not `FLOAT`.** Binary floating point makes the stored value
depend on accumulation order, so two servers writing the same sequence of deltas
can disagree in the last digits — and the value is then compared and rounded for
display. Four decimal places is more than the formula needs and costs nothing.

### 4.2 The upsert is absolute, not additive

The opposite of `Ranks_WeaponStats`, and for a reason worth stating: that module
holds session *deltas* with no read on connect, so additive is correct there.
This module reads the current rating, computes a new one, and writes it.

```sql
-- MySQL / MariaDB
INSERT INTO {t} (steam_id, period, rating, matches, abandons, updated_at)
VALUES (@steam_id, @period, @rating, @matches, @abandons, @updated_at)
ON DUPLICATE KEY UPDATE
    rating = VALUES(rating),
    matches = matches + VALUES(matches),
    abandons = abandons + VALUES(abandons),
    updated_at = VALUES(updated_at);

-- PostgreSQL / SQLite
INSERT INTO {t} (...) VALUES (...)
ON CONFLICT (steam_id) DO UPDATE SET
    rating = EXCLUDED.rating,
    matches = {t}.matches + EXCLUDED.matches,
    abandons = {t}.abandons + EXCLUDED.abandons,
    updated_at = EXCLUDED.updated_at;
```

`rating` is replaced; the two counters accumulate. A settle passes `@matches = 1`
and `@abandons` from the result; an admin's adjustment (§8.4) passes `0` for both,
so it moves the rating without counting a match, and a row it creates starts on 0
of each. Keep `VALUES()` on the MySQL branch — 8.0.20 deprecates it for the
`AS new` alias, which MariaDB does not support at all. The update leaves `period`
alone: by the time it runs, the only row it can hit already holds the write's
period.

**Every write carries the period it was settled or adjusted in**, captured on the
game thread at that moment, and lands in that period however late the store gets
to it. Per write, inside the batch's transaction:

1. `SELECT period, rating FROM {t} WHERE steam_id = @steam_id`.
2. A row on an older period is closed first (§4.3).
3. A row on a **newer** period means this write is a retry that crossed a
   rollover: it goes to `{history}`'s row for its own period — the same upsert
   with `ON CONFLICT (steam_id, period)` — and the current row is left alone.
4. No row at all, but `{history}` already holds `(steam_id, @period)`: the
   period has closed for this player since the write was settled (a read moved
   the row on the way in), so the write goes to history as well. Without this
   check it would re-open a row on a closed period, which the next read would
   close again — dropping the write.
5. Otherwise the upsert above, into `{t}`.

Table names come from `ResolveTableName` at query-build time, never string
concatenation.

### 4.3 Reading the rating in

A player's rating is needed the moment they enter a match, which may be before
they have a row. Resolution order:

1. this module's table, for the **current period**, read once per player per
   session and cached on the session;
2. core's `GetPoints` — covers a server migrating from core scoring, where the
   accumulated points are the only starting signal there is;
3. `Rank.StartPoints`.

The period is captured on the game thread and passed in. A row on an older
period is that period's final rating: in one transaction it is copied to
`{history}` (`INSERT IGNORE … SELECT` on MySQL/MariaDB, `INSERT … SELECT … WHERE
… ON CONFLICT (steam_id, period) DO NOTHING` elsewhere — SQLite needs the `WHERE`
to parse it) and deleted from `{t}`, both guarded by `period = @stale`, and the
read answers null so the player seeds as a new one. A row on a newer period —
another server sharing the database crossed the boundary first — comes back as
it is, as core treats its own rows.

**The cache is keyed by period**: an entry is `(period, rating)`, and a lookup for
any other period misses. It is also cleared on `PeriodChanged`, so it never holds
more than one period's worth. A reset leaves an entry with **no** rating for its
period, which answers "none" without a query while the delete is still queued.

The read is off-thread and must not block a round boundary. A read still in
flight when a round ends leaves the session unresolved, and an unresolved player
is simply left out of the team average rather than averaged in at a guessed
value. That is deliberate rather than tolerated: a blocking read on the game
thread is a stall, and one round of a slightly narrower average is invisible
after shrinkage. An unresolved session is never *settled*, either — see §7.3.

**The continuation marshals back through `Core.Scheduler.NextTick` before it
touches anything.** The session, core's API and the session registry are all
game-thread state, and the `await` resumes on the thread pool — applying the
result inline would race the round handler and could publish `RatingResolved`
ahead of `Rating`. It also checks the session's `RatingGeneration`: a new period
or a reset re-seeds the session and bumps it, so a read begun before either is
discarded rather than landing the wiped rating on top of the new seed.

**Core's points can arrive first.** `PlayerDataLoaded` can land before the
read, and seeds an unresolved session from core's points — `StartPoints` when
those are 0 — so the player counts in team averages at once. That seed is only a
stand-in, marked `RatingSeededFromCore`: the points are rounded, and may hold
points another plugin gave, which the next settle would carry into the rating. A
stored rating read for the same generation replaces it; a rating a settle, an
admin's change or a reset has set since is never replaced. Finding no row leaves
the seed, which is what step 2 above would choose anyway.

**The read can arrive first, too.** A read finding no row resolves the session
from step 2 or 3 only once core holds the player's record — the bridge's
`IsLoaded`. Before that, core's `GetPoints` answers 0 for a player it has not
loaded, and resolving there would start someone moving over from core scoring at
`StartPoints`, their points lost; the session waits for `PlayerDataLoaded` to seed
it instead. A player whose core record never loads stays unrated (§7.3).

**A read can fail, too.** The store reports a failure apart from finding no row:
taken for none, it would start a player who holds a rating from core's rounded
points for good, and their next match end would write that over the rating the read
never reached. So the tracker logs the player's first failure at `Error`, stands
core's points in exactly as `PlayerDataLoaded` would — marked `RatingSeededFromCore`,
and only once core holds the record — and retries the read three times, 5, 15 and
then 45 seconds apart. Each retry runs only for the same `RatingGeneration` and only
while a stored rating would still be taken, so a settle, an admin's change, a reset
or a new period ends them; one that lands replaces the stand-in. A retry that fails
again is logged only with `DetailedLogging`.

**`PlayerDataLoaded` also starts sessions.** Steam can authorize a player after
they are put in server, and `Adopt` skips a SteamID of 0, which is what an
unauthorized player reads under strict authorization. Core's own load waits for
the authorization, so when `PlayerDataLoaded` names a player with no session, the
tracker finds them connected and authorized and adopts them there. The same
picks up a player who entered while Ranks was not bound, which `Adopt` also
skips: a Ranks loaded or reloaded on a running server loads everyone connected.
Mid-match, either counts as having joined late.

### 4.4 The queue, and failure handling

**Every database operation the store makes runs on one queue**, in the order the
game thread asked for it: settles, admin adjustments, resets, purges and reads
alike. That is the whole of the ordering argument — a reset always lands after the
settle before it and before the settle after it, and a read can never close a
player's row while a write for that player is still in flight.

A failed write must not lose the rating. A failed batch stays at the head of the
queue's retry list and is applied again, ahead of the next change and in the
same transaction, never reordered — so a reset still lands between the two
settles it came between. It never merges back into a live session; those belong
to the game thread (§6).

A reset or purge the database can never apply — no `DELETE` permission, say —
would hold every later rating back for the rest of the load. So a failure is
charged to the change whose own statement threw, and a reset or purge charged
three times is dropped, logged at `Error` once, and the list behind it written
straight away. Two kinds of failure are charged to nobody: one the driver marks
transient (`DbException.IsTransient`, on the exception or any it wraps), and
anything thrown outside the change's own statements — opening, beginning or
committing the transaction, or another change. So connection failures,
deadlocks, lock waits and timeouts the driver marks transient never drop one.
That matters on PostgreSQL in particular: Npgsql sends `BEGIN` with the first
statement, so a pooled connection that died idle fails at the change's own
statement. Drivers differ in what they mark: Npgsql marks lost connections,
deadlocks, and lock and command timeouts; MySqlConnector marks deadlocks and
InnoDB lock-wait timeouts, but not a connection lost mid-statement or its own
command timeout; System.Data.SQLite marks nothing. A rating is never given up on:
it is kept until it is written.

Log at `Error` with the exception object. This is a data-loss path and is not
gated behind `DetailedLogging`.

---

## 5. The match state machine

### 5.1 Reading match state safely

```csharp
// GameRules only exists after world spawn; reading MatchData between maps, during
// bootup, or once the rules entity has been torn down throws — and some SwiftlyS2
// builds throw NullReferenceException rather than anything specific.
private bool TryReadMatch(out MatchSnapshot snapshot)
{
    try
    {
        ref var m = ref Core.Game.MatchData;
        snapshot = new MatchSnapshot(m.ActualRoundsPlayed, m.Phase, m.CTScoreTotal, m.TerroristScoreTotal);
        return true;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "MatchData unavailable.");
        snapshot = default;
        return false;
    }
}
```

**The broad catch is not defensive padding — it is a production finding** from
a server-reporting plugin that reads the same struct on a heartbeat. It matters
more here than there: this module reads at round end and map unload, which is
precisely when the rules entity is being torn down.

A failed read at a round boundary skips that round's accrual rather than
guessing. A failed read at settle time falls back to the session's own round
count.

### 5.2 Boundaries

| Boundary | Signal |
|---|---|
| match start | first live round after `Phase` leaves `GAMEPHASE_WARMUP_ROUND`, or `ActualRoundsPlayed` moving backwards |
| round end | `EventRoundEnd`, filtered by core's play gate |
| match end | `EventCsWinPanelMatch`, **or** `Phase == GAMEPHASE_MATCH_ENDED` |
| abandoned | `OnMapUnload`, or `Unload`, with a match still open |
| cut by a season | `PeriodChanged` with a round already accrued — the rest of the match is not rated (§8.2) |

Match end is deliberately double-sourced. The event is primary; the phase poll
is a backstop, and settling is idempotent per match so firing both is harmless.
The failure it guards is that *nothing is ever written for any match*, which is
the one outcome that must not be silent.

A round is rated only when core counted it (FR-PTS-03). `OnRoundEnd` asks core's
`IsPlayCounted` inside the same dispatch; core decides it once per tick, so the answer
is the one core used for its own round result, and it covers `Rank.MinPlayers`,
warmup and a team made up only of bots at once. A draw is skipped without asking, as
core skips it. Adding a gate of its own here would rate play core's statistics left
out, or leave unrated play they kept.

Against a Ranks older than 1.0.4 the call fails at JIT and the module falls back to its
own rules: warmup rounds are filtered on `Phase == GAMEPHASE_WARMUP_ROUND` and gated by
core's `Rank.WarmupPoints`, and settling applies the match-level `MinPlayers` and
no-rated-humans checks.

### 5.3 Restart detection

```csharp
// Source-agnostic: a knife-round plugin, an admin, or a match plugin all look
// identical from here, and none of them has to be hooked.
if (snapshot.RoundsPlayed < _lastRoundsPlayed) DiscardMatch("restart");

_lastRoundsPlayed = snapshot.RoundsPlayed;
```

> **Confirmed on a live server (2026-10-26): `ActualRoundsPlayed` resets on
> `mp_restartgame`.** The branch above is the whole of restart detection, and a
> knife round is discarded rather than counted.

Still not covered: a knife round followed by `mp_swapteams` and a continue, with
no restart at all. That counts one phantom round, accepted deliberately — one
round in ~24, which shrinkage renders negligible.

`Nm` comes from `ActualRoundsPlayed` at settle time. Mid-match — for a
participation factor that needs a denominator before the match is over — it
comes from the `mp_maxrounds` convar, falling back to `Rating.ExpectedRounds`
only when the convar cannot be read.

---

## 6. Round accumulation

### 6.1 The session

```csharp
private readonly Dictionary<ulong, MatchSession> _sessions = new();
```

**A dictionary keyed on SteamID, not the 64-slot array WeaponStats uses.** The
slot array is faster, and it is wrong here: a session must survive a disconnect
so a reconnect resumes it (features.md §2.2), and the slot is cleared and
reissued on disconnect. The access rate justifies it — once per player per round
rather than once per bullet.

```csharp
internal sealed class MatchSession
{
    public required ulong SteamId { get; init; }
    public double Rating;            // resolved per §4.3, then moved by every settle (§7.3)
    public bool RatingResolved;
    public bool RatingSeededFromCore;  // Rating is core's points standing in until the read lands (§4.3)
    public int RatingGeneration;     // bumped on every re-seed, so a stale read is discarded
    public int RoundsPresent;
    public double SumActual;         // Σ S_r
    public double SumExpected;       // Σ E_r
    public PerfCounters? StartCounters;
    public int LastTeam;             // so a departed player can still be attributed at settle
    public bool Present = true;      // false once disconnected or moved to spectator
}
```

All of it is touched only on the game thread. A match boundary resets each
session in place rather than clearing the dictionary — see §6.3.

### 6.3 Resetting between matches

**Clearing `_sessions` at a match boundary is wrong, and it looks right.**
Nothing re-adds a session for an already-connected player: `Adopt` runs on
connect, when core loads a player's record, and once after a load into a running
server. So wiping the dictionary after a knife-round
restart, or after any settle, leaves every connected player sessionless, and the
entire following match accrues nothing and settles nobody.

`StartFreshMatch()` instead walks the existing sessions and:

- drops the ones marked absent (they have just been settled or have left);
- zeroes `RoundsPresent`, `SumActual`, `SumExpected`, `LastTeam`;
- keeps `Rating` / `RatingResolved` — after a settle, `Rating` is the rating
  that match settled at (§7.3) — so no database re-read is needed;
- **re-captures `StartCounters`.**

That last point is a second bug in the same place. `StartCounters` is a *match*
baseline, and it is first taken on `PlayerDataLoaded` — i.e. at connect. Left
alone, a player's second match would be scored on counters spanning both, and
the performance percentile would be meaningless for everyone who stayed.

It deliberately does not touch `_settled`: `Settle` sets that flag and calls
`StartFreshMatch`, so resetting it there would let the 5-second backstop settle
the same match repeatedly. `_settled` is cleared by the first accrued round of
the next match instead.

The per-session half is `Rebaseline`, which a reset also runs for one player
(§8.3). A new period runs `StartFreshMatch` even between matches: core has just
zeroed every counter the baselines were taken from, and a baseline from before
would score the next match's play as negative (§8.2).

### 6.2 Per round

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnRoundEnd(EventRoundEnd e)
{
    if (!TryReadMatch(out var m)) return HookResult.Continue;
    if (m.RoundsPlayed < _lastRoundsPlayed) { DiscardMatch("restart"); return HookResult.Continue; }
    _lastRoundsPlayed = m.RoundsPlayed;

    // Only the rounds core counted (FR-PTS-03). A draw is nobody's result.
    if (e.Winner is not ((int)Team.T or (int)Team.CT) || !_core.IsPlayCounted) return HookResult.Continue;

    var (avgT, avgCt) = TeamAverages();

    foreach (var player in Core.PlayerManager.GetAllPlayers())
    {
        if (_sessions.GetValueOrDefault(player.SteamID) is not { Present: true } s) continue;

        var own = player.Controller.TeamNum == (int)Team.T ? avgT : avgCt;
        var opp = player.Controller.TeamNum == (int)Team.T ? avgCt : avgT;

        s.SumExpected += EloMath.Expected(own, opp);
        s.SumActual += player.Controller.TeamNum == e.Winner ? 1.0 : 0.0;
        s.RoundsPresent++;
    }

    return HookResult.Continue;
}
```

**Team averages are recomputed every round**, which is what makes a team that
loses a player mid-match be measured against its current strength. It is a pass
over at most 10 players once per round.

A spectator has no session marked `Present`, so the loop skips them without a
team check.

### 6.3 Imputing bots and unrated players

```csharp
// Order matters: own team, then whole match, then the configured seed. Each
// fallback is only reached when the one above it has no rated player at all.
private double ImputedRating(int teamNum) =>
    MeanRatedOn(teamNum) ?? MeanRatedInMatch() ?? _core.StartPoints;
```

With core's `Rank.BotsEnabled` off, a round against a team made up only of bots is
never accrued, because core does not count it (features.md §3). Against a Ranks older
than 1.0.4, which cannot report that, a settle-time check stands in: a match where one
side had no rated humans is unrated. It runs at settle rather than per round because a
team can legitimately have no rated humans for a few rounds and then gain one.

---

## 7. Settling

### 7.1 Order of operations

Fixed, and each step depends on the one before it:

```
1.  base   = K * (S_adj - E_mean) * (n / Nm)
2.  base   = clamp(base, -K * E_mean, K * (1 - E_mean))     # envelope
3.  delta  = base + B * (percentile - 0.5)                  # bonus
4.  delta  = SignFloor(base, delta)                         # bonus cannot flip base's sign
5.  delta -= Abandon.Penalty                                # only if absent at match end
6.  rating = max(rating + delta, MinPoints)
```

Steps 2 and 4 are both required and neither subsumes the other — features.md
§1.5 has the worked case the envelope misses.

Step 5 is outside the sign floor on purpose: the penalty is not part of the
result, and it is allowed to turn a win into a net loss. That is the entire
point of it.

```csharp
private static double SignFloor(double base_, double delta) => base_ switch
{
    > 0 when delta < Epsilon  => Epsilon,
    < 0 when delta > -Epsilon => -Epsilon,
    _ => delta,
};
```

`Epsilon` is a constant, not a config key — it is a correctness guard, not a
tuning knob.

### 7.2 The performance percentile

```
raw = kills + 0.5*assists - 0.7*deaths + 0.3*roundWins + 2*bombsPlanted + 2*bombsDefused
```

…over the **match delta** of each counter, then ranked within the match and
mapped to `[0, 1]`.

Every counter comes from `GetSnapshot` diffed between match start and match end.
`GetStat` exposes the same numbers but they are lifetime totals, so a long-tenured
player would score higher on their history rather than on this match.

**Mid-match joiners are snapshotted on `PlayerDataLoaded`**, not at match start —
that event is the only moment their record is guaranteed resident. A player whose
start counters were never captured scores at the median (`p = 0.5`), contributing
no bonus either way, rather than being scored against a zero baseline that would
read as a perfect game.

Objectives are weighted in because a pure K/D measure pays players to stop
defusing (features.md §1.4). The coefficients are a starting point and are
expected to move once there is real data; they live in one `const` block for
that reason.

### 7.3 Writing

Settling runs on the game thread. Core is updated inline; the database write is
queued and leaves the thread:

```csharp
var period = _bridge.CurrentPeriod();             // captured here, travels with every write
// ...
_bridge.SetPoints(steamId, (int)Math.Round(updated), "elopoints.match");
_bridge.SavePlayer(steamId);
writes.Add(new RatingWrite(steamId, period, updated, abandoned));
// ...
_ = _store.CommitAsync(writes);     // off-thread from here on
CarryRatingsForward(rated, writes); // every rated session now holds its settled rating
StartFreshMatch();
```

`SetPoints` has to be inline because core's API is game-thread state; the DB
write must not be, because a synchronous write at match end is a stall.

**Every rated session moves to its settled rating, after the loop.** A session
outlives the match for a player who stays — `StartFreshMatch` keeps it, and a
map change's reconnect resumes it — so the next match is rated from this one's
result. Left alone, a player who stayed was rated from the rating they joined
with every match, and each settle overwrote the last. Never inside the loop:
`Classic` scores every player against `TeamAverages`, which reads every
session's rating, so moving one mid-loop would rate the players after it
against teammates already moved.

**Only a session whose rating resolved is settled.** One that never resolved
holds either nothing — a read that did not land in a whole match — or, after a
re-seed, the rating a new period or a reset just wiped; settling it would write
`MinPoints + delta` or the wiped rating straight back. Such a player is left
unrated for the match, with one ungated warning, because a read that never lands
is a database problem worth seeing.

**The module's table is still the authoritative value** (features.md §5) even
though core is updated first. `CommitAsync` populates the store's cache before
it queues anything, so a player who had already disconnected — and whom core may
no longer track, making `SetPoints` a no-op — is reconciled on their next
`PlayerDataLoaded`, as long as the cached rating belongs to the period core has
just put them in:

```csharp
if (_bridge.CurrentPeriod() is { } period
    && _store.TryGetCached(player.SteamId, period, out var rating)
    && (int)Math.Round(rating) != player.Points)
    Api.SetPoints(player.SteamId, (int)Math.Round(rating), "elopoints.resync");
```

The resync is also what overwrites every point change the module does not adopt.
An admin's change is the one it does: it reaches the cache before the player's next
join, so the resync finds the two in agreement (§8.4).

`SavePlayerAsync` is called explicitly rather than waiting for core's periodic
pass, per FR-MOD-05 rule 5 — the record is evicted once saved, and the next pass
may be minutes away.

**Rounding, never truncation.** `(int)rating` would bias every write downward and
the error would accumulate in one direction forever, which is the whole reason
the `DECIMAL` column exists.

---

## 8. Purge, periods and resets

### 8.1 Purge

```csharp
protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _store.PurgeAsync(steamIds);
```

Core has already removed its own rows by the time this fires, so the module must
not expect to look the player up. The purge deletes from **both** tables — a
purged player loses their history too, as in core (FR-DB-10) — and drops
anything of theirs still waiting in the retry list, which would otherwise
re-create a row for a player core no longer has.

The purge is a queued change like any other (§4.4), and one that fails is kept
rather than dropped: this module has no orphan sweep, so nothing else would ever
remove those rows. It stays in the retry list ahead of everything queued after
it, so a rating written for one of those players since lands on the cleared rows
instead of being deleted with them, and while it waits, a read for one of them
answers none rather than their old rating. Dropped after the third failure of its
own statements the driver does not mark transient (§4.4), it leaves those rows,
and a returning player reads their old rating again.

### 8.2 A new period

Core raises `PeriodChanged` after it has moved `CurrentPeriod` and put every
resident player back on `StartPoints` (FR-API-13). The module's own rating would
undo that at the next match end, so `OnPeriodChanged`:

1. **clears the rating cache** — every entry belongs to the period that closed;
2. **leaves a match the boundary cut through unrated.** If any session has
   accrued a round, the match is discarded and marked void: later rounds of it
   accrue nothing, and the void clears when that match settles, restarts or the
   map changes. Its rounds were measured against last period's ratings, and what
   is left after the boundary is not a whole match;
3. **re-baselines every session's counters**, match or no match — core has just
   zeroed them;
4. **re-seeds every session**: `RatingResolved` goes false, `RatingGeneration` is
   bumped, and the rating is read again for the new period. That read closes the
   player's old row on the way in (§4.3), so an online player's final rating for
   the closed period reaches history at the boundary, and they start the new one
   from core's points — `StartPoints`, which core has just set.

Step 4 is what the discard alone does not do: `StartFreshMatch` keeps each
session's rating on purpose (§6.3), so without a re-seed the next match would
settle last period's rating into this one.

Core can raise `PeriodChanged` from inside its own `round_end` dispatch — the
rollover runs at round end — so the handler reads no native state (§2.3a). The
discard is logged at `Information` behind `DetailedLogging`, like every other
discard: a season boundary is expected behaviour, and core already announces it
to the server.

### 8.3 A reset

`!resetrank`, `!resetmyrank` and the Settings reset wipe the player's current
period in core, and core raises `PlayerReset` afterwards. `OnPlayerReset`:

1. **queues a delete of the player's rating for the current period** behind every
   write already queued (§4.4), from `ranks_elopoints` and — should the period
   have closed before the delete got its turn — from `ranks_elopoints_history`.
   Past periods stay;
2. **marks the cache "no rating" for that period**, so a read while the delete
   waits cannot find the old row;
3. **re-seeds the player's session in place** from core's points — 0 after a
   reset, so `StartPoints` — without a read, and drops the rounds it had accrued.

The session is re-seeded rather than removed because nothing re-adds a session for
a player who is still connected (§6.3): removing it would leave them unrated until
they reconnected. Its accrued rounds go because they were measured against the
rating the reset wiped; the player is rated on the rest of the match as if they had
joined at that moment, which participation and shrinkage already handle.

### 8.4 An admin's change

`OnPointsChanged` adopts exactly three reasons, the ones core's admin commands
write (FR-CMD-03): `admin.set`, `admin.add`, `admin.subtract`. Everything else —
the module's own `elopoints.*` writes, another plugin's `AddPoints` or `SetPoints`
— is left alone, and the next resync or settle writes the rating back over it.
`MatchTracker.AdoptAdminChange`, on the game thread:

1. **works out the rating.** `admin.set` takes `NewPoints`. A give or a take adds
   `Delta` — what core applied after its `MinPoints` floor — to the rating the
   module knows for the current period: the store's cache first, which every read,
   settle and adjustment updates, else a resolved session's, else core's
   `OldPoints`. That last is a trade-off: while the rating is unresolved, a give or
   a take lands on top of core's points rather than the stored rating, so a
   non-admin change already in them can stick. The result floors at `MinPoints`,
   so a fractional rating cannot go below it where core's points did not;
2. **caches and queues it** with `RatingStore.AdjustAsync`: cached at once, so the
   resync on the next join agrees and a read already in flight cannot replace it
   (`Remember` keeps a same-period entry); written on the one queue (§4.4) with the
   period captured here, through the same period rules as a settle (§4.2), but
   adding 0 to `matches` and `abandons`;
3. **re-seats the session**, if the player has one: `Rating` set, `RatingResolved`
   true and `RatingGeneration` bumped, so a read begun before the change is
   discarded (§4.3). The rounds already accrued stay — the admin changed the rating
   the match settles from, not the match.

Without this, the change lasted only until the rating was next written: the
resync on the player's next join pushed the cached rating straight back over it,
which is how a `!setpoints` to 1000 came back as 54 after a map change.

Core reports `!setpoints` even to the total the player already has, with `Delta`
0 (FR-CMD-03), and `admin.set` takes `NewPoints` either way. So setting a player to
the points they show sets the rating too — the case that matters once another
plugin's points have moved the total off the rating.

**The published API does not count the change as a standing.** A row the write
created holds `matches = 0` until its player completes a rated match in that
period, and `IEloPointsApi` promises a standing only after one. So every
`EloPointsApi` read filters on `matches > 0` — the current period's and a closed
period's, history included, since a rollover archives such a row like any other.
The module's own reads are unfiltered: the resync and the session seed take the
adjusted rating.

---

## 9. Rating invariants

Nothing asserts these at runtime. Every one of them is **silent when broken** —
the module keeps running and the numbers stay plausible — so check them by reading
whenever `EloMath` changes.

| Invariant | What breaking it looks like |
|---|---|
| `Expected(r, r) == 0.5`; a 400-point gap gives ≈ `0.9091`; `Expected(a,b) + Expected(b,a) == 1` | an inverted sign in the exponent, invisible on even teams |
| Shrinkage with `n = 0` yields `S_adj == E`, giving zero delta | a division that produces `NaN` on an empty session |
| Shrinkage is monotonic in `n` toward the unshrunk value | an inverted weighting that damps *long* sessions instead of short ones |
| 13-0 even teams, `c = 10` → `+14.13`; full 13-8 → `+4.03`; join-at-18 4-0 → `+1.36`; 2-round cameo → `+0.40` | coefficient drift away from the figures published in features.md |
| A double-counted accrual (`S̄ = 2.0`) still respects the envelope ceiling | the envelope clamp removed or mis-bounded |
| `E = 0.95`, win, worst percentile → delta is positive | the sign floor missing, which the envelope does not catch |
| Abandon penalty always reduces the delta by exactly `Penalty` | the penalty folded inside the sign floor |
| Four identical performances all land on the median | ties not sharing a rank, ordering identical play arbitrarily |

> **The envelope invariant only shows up on corrupted input.** With an actual
> score in `[0, 1]`, `K(S_adj − E)` already lands inside the envelope
> algebraically, so the clamp is unreachable on valid data. It exists to bound an
> upstream accrual bug — a round credited twice. Deleting it passed a 20 000
> iteration randomised sweep, which is why reading beats sampling here.

---

## 10. Open items

| Item | Blocking? | Note |
|---|---|---|
| `EventCsWinPanelMatch` firing on every match configuration | no | phase backstop covers it (§5.2) |
| Performance coefficients (§7.2) | no | starting values; expected to move on real data |
| Placement matches | no | deliberately out of scope (features.md §6) |
| `Abandon.Penalty` does not deter abandonment in the first third of a lopsided match | no | bounded and documented (features.md §2.3); closing it needs `Penalty ≈ K × 0.9` |

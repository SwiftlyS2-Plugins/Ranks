<div align="center">
  <h1><strong>Ranks</strong></h1>
  <p>Points, ranks and gameplay statistics for CS2 — with a module API other plugins build on.</p>
</div>

<p align="center">
  <a href="https://github.com/shmitzas/Ranks/releases/latest"><img src="https://img.shields.io/github/v/release/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Latest Release"></a>
  <img src="https://img.shields.io/badge/SwiftlyS2-1.5.1--beta.12-blue?style=flat" alt="SwiftlyS2 Version">
  <img src="https://img.shields.io/github/downloads/shmitzas/Ranks/total?style=flat&logo=github&cacheSeconds=3600" alt="Downloads">
  <img src="https://img.shields.io/github/stars/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Stars">
</p>

---

## Features

- 🏆 **Points and ranks** - a configurable ladder, priced per event, with VIP and dynamic multipliers
- 📊 **Gameplay stats** - kills, deaths, assists, round and match results, bomb plants and defuses
- 🎖️ **Native scoreboard ranks** - Premier, Competitive, Wingman, Danger Zone, or a Custom mode for replacement icon sets, plus rank clan tags
- 🗄️ **MySQL, PostgreSQL or SQLite** - one connection, shared with every module
- 🧩 **Module API** - other plugins read ranks, react to changes, and own tables core migrates for them
- 🔁 **Live config reload** - all configuration changes apply on save
- 🏷️ **Multi-server friendly** - table prefixes and suffixes let several servers share one database

---

## Modules

Core publishes `IRanksApi` as a SwiftlyS2 shared interface. A module can read points and stats,
subscribe to point and rank changes, share core's database connection, and hand core its own
migrations to run. Modules own their tables; core only runs the migrations.

| Module | Kind | What it adds |
|--------|------|--------------|
| [Ranks_WeaponStats](Modules/Ranks_WeaponStats) | data | Per-weapon and per-hitbox statistics, with `!weaponstats` |
| [Ranks_EloPoints](Modules/Ranks_EloPoints) | absolute | Faceit-style Elo rating in place of per-kill scoring |
| [Ranks_CustomCombos](Modules/Ranks_CustomCombos) | additive | Operator-defined kill combinations, with `!combos` |
| [Ranks_Prestige](Modules/Ranks_Prestige) | display | Prestige levels as a virtual point deduction, with `!prestige` |

Only the two scoring kinds can conflict. An **additive** module adds points alongside core's
scoring. An **absolute** module replaces it entirely — set `Rank.UseCoreRankingLogic` to `false`
and the module becomes the only thing writing points, while core keeps persistence, the ladder,
the scoreboard and the commands.

> [!WARNING]
> **At most one absolute module may run at a time, and core does not police this.** Two of them
> overwrite each other's rating with no error anywhere.

A **data** module only records, and a **display** module only changes what the scoreboard shows;
neither writes points, so both compose with anything.

Modules that publish a read API of their own ship their own contract assembly, from their own
directory — adding one never touches core.

---

## Requirements

- SwiftlyS2
- A database connection in SwiftlyS2's `configs/database.jsonc` — MySQL, MariaDB, PostgreSQL or SQLite

Tables are created on first load. There is nothing to import by hand.

---

## Installation

1. Drop the plugin folder into `addons/swiftlys2/plugins/`.
2. Make sure `configs/database.jsonc` has the connection you want to use.
3. Restart the server. Core creates its tables and runs any module migrations.
4. Four config files appear on first load — see below.

> [!NOTE]
> **An unknown `Database.Connection` is a hard failure, deliberately.** SwiftlyS2 would
> silently fall back to the default connection, which means a typo quietly writes every rank to the
> wrong database. Core opens the named connection at boot and refuses to start instead.

---

## Commands

### Player Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!rank` | `!myrank` | None | Your rank, points and position |
| `!ranks` | `!ranklist` | None | The full ladder |
| `!resetmyrank` | - | None | Reset your own rank to the starting points |
| `!settings` | `!options` | None | Per-player preferences menu |
| `!stats` | `!mystats` | None | Your lifetime counters |
| `!togglepointmsg` | - | None | Turn your own point messages on or off |
| `!top` | `!ranktop` | None | The leaderboard |

### Admin Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!givepoints <player> <amount>` | - | `ranks.admin` | Add points to a player |
| `!removepoints <player> <amount>` | - | `ranks.admin` | Subtract points from a player |
| `!resetrank <player>` | - | `ranks.admin` | Reset a player's rank |
| `!setpoints <player> <amount>` | - | `ranks.admin` | Set a player's points outright |

Names, aliases and permissions are all editable in `commands.jsonc`. An empty `Command` removes
the command entirely.

---

## Configuration

Four files, all created on first load, all reloaded on save. Each is generated from core's own
model with explanatory comments; the values below are what ships.

| File | Holds |
|------|-------|
| `config.jsonc` | Database, gates, scoreboard, multipliers, general behaviour |
| `points.jsonc` | What each event is worth |
| `ranks.jsonc` | The rank tiers |
| `commands.jsonc` | Command names, aliases and permissions |

### `config.jsonc`

<details open>
<summary>Default <code>config.jsonc</code></summary>

```jsonc
{
  "Ranks": {
    "Database": {
      "Connection": "default",
      "TablePrefix": "",
      "TableSuffix": "",
      "SaveIntervalSeconds": 300,
      "PurgeDays": 0,
      "MaintenanceIntervalHours": 0
    },
    "Rank": {
      "UseCoreRankingLogic": true,
      "MinPlayers": 4,
      "MinPoints": 0,
      "StartPoints": 0,
      "WarmupPoints": false,
      "PointsForBots": false,
      "FfaMode": false
    },
    "Scoreboard": {
      "RankMode": "Premier",
      "ScoreSync": false,
      "Clantags": true,
      "SkillGroupOffset": 0,
      "CustomRankBase": 0,
      "CustomRankMax": 18,
      "CustomRankMargin": 0
    },
    "Points": {
      "DynamicKillPoints": true,
      "DynamicKillMin": 0.5,
      "DynamicKillMax": 3.0,
      "DynamicDeathPoints": true,
      "DynamicDeathMin": 0.5,
      "DynamicDeathMax": 3.0,
      "LongDistanceUnits": 2000,
      "SecondsBetweenKills": 0,
      "KillstreakResetOnRoundEnd": true,
      "ShowPlayerNames": false,
      "KillStreaksEnabled": true,
      "RoundEndSummary": false,
      "ShowPointMessages": true,
      "ShowRankChangeMessages": true
    },
    "Vip": {
      "Multiplier": 1.25,
      "Flags": [ "@ranks/vip" ]
    },
    "General": {
      "DetailedLogging": false
    }
  }
}
```

</details>

#### Database

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Database.Connection` | string | `"default"` | Entry in SwiftlyS2's `configs/database.jsonc` |
| `Database.TablePrefix` | string | `""` | Prefix for every table, so several servers can share one database |
| `Database.TableSuffix` | string | `""` | Suffix for every table |
| `Database.SaveIntervalSeconds` | int | `300` | How often dirty records are written |
| `Database.PurgeDays` | int | `0` | Delete players unseen this long; `0` never purges |
| `Database.MaintenanceIntervalHours` | int | `0` | `0` runs roll-up and purge on cold load only; above zero also on an empty server |

Prefix and suffix are normalised to one underscore, so `awp`, `awp_` and `_awp` all agree.

#### Rank

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Rank.UseCoreRankingLogic` | bool | `true` | `false` hands scoring to a rating module and leaves core as the host |
| `Rank.MinPlayers` | int | `4` | Minimum players before points are awarded |
| `Rank.MinPoints` | int | `0` | Floor a player cannot fall below |
| `Rank.StartPoints` | int | `0` | Points a new player begins with |
| `Rank.WarmupPoints` | bool | `false` | Count warmup at all — `false` discards warmup deaths entirely |
| `Rank.PointsForBots` | bool | `false` | Count kills, deaths and assists involving bots |
| `Rank.FfaMode` | bool | `false` | Suppress team-damage penalties on free-for-all servers |

> [!NOTE]
> **`WarmupPoints` and `PointsForBots` discard the death, not just the points.** `MinPlayers` and
> `FfaMode` suppress the *award* and still record the kill — that happened between real players.
> With `WarmupPoints` or `PointsForBots` off, the matching deaths are dropped outright: no kills,
> deaths or assists, and no killstreak. A warmup round is not the match, and a bot is not a player
> — counting either made `!top kills` and the streak tiers farmable against AI.

#### Scoreboard

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Scoreboard.RankMode` | string | `"Premier"` | `Off`, `Premier`, `Competitive`, `Wingman`, `DangerZone` or `Custom` |
| `Scoreboard.ScoreSync` | bool | `false` | Write points into the scoreboard score column |
| `Scoreboard.Clantags` | bool | `true` | Show the rank tag as a clan tag |
| `Scoreboard.SkillGroupOffset` | int | `0` | Shifts the icon; **Competitive, Wingman and Danger Zone only** |
| `Scoreboard.CustomRankBase` | int | `0` | First icon index of a replacement set; **Custom mode only** |
| `Scoreboard.CustomRankMax` | int | `18` | Ladder position past which everyone shares the top icon; **Custom mode only** |
| `Scoreboard.CustomRankMargin` | int | `0` | Ladder positions skipped before the first icon; **Custom mode only** |

`Premier` renders the point total itself as a CS Rating, coloured by CS2's own bands — which is
why the shipped seven-band ladder is legible without mapping anything. `Competitive` renders an
icon instead, so it wants an 18-band ladder; one ships at
`resources/examples/ranks-competitive.jsonc`.

#### Points

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Points.DynamicKillPoints` | bool | `true` | Scale kill rewards by the rank gap |
| `Points.DynamicKillMin` / `Max` | double | `0.5` / `3.0` | Bounds on that scaling |
| `Points.DynamicDeathPoints` | bool | `true` | Scale death penalties by the rank gap |
| `Points.DynamicDeathMin` / `Max` | double | `0.5` / `3.0` | Bounds on that scaling |
| `Points.LongDistanceUnits` | int | `2000` | Source units for a long-distance kill (~50 m) |
| `Points.SecondsBetweenKills` | int | `0` | Streak window; `0` lets the round bound it |
| `Points.KillstreakResetOnRoundEnd` | bool | `true` | Clear streaks at round end |
| `Points.ShowPlayerNames` | bool | `false` | Name the other player in point messages |
| `Points.KillStreaksEnabled` | bool | `true` | Award killstreak bonuses |
| `Points.RoundEndSummary` | bool | `false` | Batch point messages into one round-end line |
| `Points.ShowPointMessages` | bool | `true` | Server default for per-point chat messages |
| `Points.ShowRankChangeMessages` | bool | `true` | Announce promotions and demotions |

#### VIP and General

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Vip.Multiplier` | double | `1.25` | Applied to every core award for flag holders |
| `Vip.Flags` | array | `["@ranks/vip"]` | Flags that earn the multiplier |
| `General.DetailedLogging` | bool | `false` | Narrate expected flow; warnings and errors always print |

### `points.jsonc`

One entry per event — 33 of them, plus eleven killstreak tiers. A value of `0` disables the event
and its chat line, but **not** its stat increment.

<details open>
<summary>Default <code>points.jsonc</code></summary>

```jsonc
{
  "Points": {
    "Values": {
      "core.kill": 2,
      "core.death": -3,
      "core.assist": 1,
      "core.assist_flash": 1,
      "core.headshot": 3,
      "core.suicide": 0,
      "core.team_kill": 0,
      "core.team_assist": 0,
      "core.noscope": 5,
      "core.wallbang": 4,
      "core.through_smoke": 3,
      "core.blind_kill": 3,
      "core.long_distance": 3,
      "core.knife_kill": 5,
      "core.taser_kill": 5,
      "core.grenade_kill": 5,
      "core.inferno_kill": 5,
      "core.impact_kill": 5,
      "core.bomb_plant": 10,
      "core.bomb_defuse": 10,
      "core.bomb_defuse_others": 10,
      "core.bomb_exploded": 10,
      "core.bomb_pickup": 2,
      "core.bomb_drop": -2,
      "core.hostage_rescue": 10,
      "core.hostage_rescue_all": 10,
      "core.hostage_hurt": 0,
      "core.hostage_kill": -20,
      "core.round_win": 2,
      "core.round_lose": -2,
      "core.mvp": 10,
      "core.match_win": 15,
      "core.match_lose": -5,

      // Killstreak tiers: 2 kills through 12-or-more, all in one round.
      "core.streak_double": 5,
      "core.streak_triple": 6,
      "core.streak_domination": 7,
      "core.streak_rampage": 8,
      "core.streak_mega": 9,
      "core.streak_ownage": 10,
      "core.streak_ultra": 11,
      "core.streak_killing_spree": 12,
      "core.streak_monster": 13,
      "core.streak_unstoppable": 14,
      "core.streak_godlike": 15
    }
  }
}
```

</details>

### `ranks.jsonc`

The rank tiers, each with a name, tag, colour and point threshold. The first tier must start at
`0`. Ships as a seven-band Premier ladder; an 18-band Competitive one is in
`resources/examples/ranks-competitive.jsonc`.

<details open>
<summary>Default <code>ranks.jsonc</code></summary>

```jsonc
{
  "Ladder": {
    "Tiers": [
      { "Name": "Grey", "Tag": "Grey", "Color": "grey", "Hex": "#B0C3D9", "Points": 0, "SkillGroup": 1 },
      { "Name": "Light Blue", "Tag": "LtBlue", "Color": "lightblue", "Hex": "#5E98D9", "Points": 5000, "SkillGroup": 4 },
      { "Name": "Blue", "Tag": "Blue", "Color": "blue", "Hex": "#4B69FF", "Points": 10000, "SkillGroup": 7 },
      { "Name": "Purple", "Tag": "Purple", "Color": "purple", "Hex": "#8847FF", "Points": 15000, "SkillGroup": 10 },
      { "Name": "Pink", "Tag": "Pink", "Color": "magenta", "Hex": "#D32CE6", "Points": 20000, "SkillGroup": 13 },
      { "Name": "Red", "Tag": "Red", "Color": "red", "Hex": "#EB4B4B", "Points": 25000, "SkillGroup": 16 },
      { "Name": "Gold", "Tag": "Gold", "Color": "gold", "Hex": "#CAAB05", "Points": 30000, "SkillGroup": 18 }
    ]
  }
}
```

</details>

### `commands.jsonc`

Every command's name, aliases and permission. An empty `Command` removes it; an empty `Permission`
means everyone.

<details open>
<summary>Default <code>commands.jsonc</code></summary>

```jsonc
{
  "Commands": {
    "All": {
      "rank":           { "Command": "rank",           "Aliases": [ "myrank" ],   "Permission": "" },
      "ranks":          { "Command": "ranks",          "Aliases": [ "ranklist" ], "Permission": "" },
      "top":            { "Command": "top",            "Aliases": [ "ranktop" ],  "Permission": "" },
      "stats":          { "Command": "stats",          "Aliases": [ "mystats" ],  "Permission": "" },
      "settings":       { "Command": "settings",       "Aliases": [ "options" ],  "Permission": "" },
      "resetmyrank":    { "Command": "resetmyrank",    "Aliases": [],             "Permission": "" },
      "togglepointmsg": { "Command": "togglepointmsg", "Aliases": [],             "Permission": "" },

      "setpoints":      { "Command": "setpoints",      "Aliases": [], "Permission": "ranks.admin" },
      "givepoints":     { "Command": "givepoints",     "Aliases": [], "Permission": "ranks.admin" },
      "removepoints":   { "Command": "removepoints",   "Aliases": [], "Permission": "ranks.admin" },
      "resetrank":      { "Command": "resetrank",      "Aliases": [], "Permission": "ranks.admin" }
    }
  }
}
```

</details>

> [!NOTE]
> **Aliases are appended to the defaults, not replaced.** The config binder adds the file's
> entries onto the model's own, so an alias cannot be removed by deleting its line — the default
> comes back. Core de-duplicates the result, so the shipped file binding each alias twice is
> harmless. To retire a command entirely, set its `Command` to `""`.

---

## How It Works

**Overview**

Every scored event runs through one pipeline: gates, base value, occurrence count, multipliers,
a single rounding at the end, then the write and the chat line.

**Rounding happens once, away from zero.** Rounding per stage lets a small award decay to nothing
through the multipliers, and a death penalty that rounds to `0` is a free death.

**Points are a judgement, counters are facts.** A disabled event, `MinPlayers`, `FfaMode`, or
`UseCoreRankingLogic = false` all stop the *award* while the stat counters keep running. The two
exceptions are `WarmupPoints` and `PointsForBots`, which discard the death entirely — see above.

**Persistence**

Records live in memory while a player is connected and are written every `SaveIntervalSeconds`,
on disconnect, and on map change. The in-memory record is authoritative, so core writes absolute
values rather than deltas.

---

## For plugin developers

Core publishes `Ranks.Api.v1`. Reference `Ranks.Contract.dll` from core's `resources/exports/` —
**do not copy it next to your plugin**, or the host loads a second one, the interface gets two type
identities, and binding silently stops matching.

```csharp
public interface IRanksApi
{
    const string InterfaceKey = "Ranks.Api.v1";

    bool IsCoreReady { get; }
    event Action CoreReady;

    int  GetPoints(ulong steamId);
    void AddPoints(ulong steamId, int amount, string reason, bool showMessage = true);
    void SetPoints(ulong steamId, int points, string reason);

    Task<IReadOnlyList<RanksLeaderboardEntry>> GetTopAsync(string statKey = "points", int limit = 10);
    Task<int> GetPositionAsync(ulong steamId, string statKey = "points");

    event Action<RanksPointsChangingArgs> PointsChanging;   // set Cancel, or amend Amount
    event Action<RanksPointsChangedArgs>  PointsChanged;
    event Action<RanksRankChangedArgs>    RankChanged;

    string ConnectionName { get; }
    string ResolveTableName(string baseName);
    void   RegisterMigrations(Assembly assembly, string pluginId);

    void SetKillArbiter(IRanksKillArbiter? arbiter);
}
```

Three rules matter more than the surface:

- **Inherit `RanksModuleBase`** rather than calling the API raw. It supplies `Table(name)`,
  `RegisterMigrations(assembly)` and the `OnCoreReady` / `OnPlayerDataLoaded` hooks already wired
  to the handshake.
- **Never name a contract type outside one bridge file.** A field or constant of a contract type
  makes the containing class a JIT hazard on a server with no Ranks installed, and that fails the
  *whole* host, not just your plugin. Keep the single file that names them behind
  `[MethodImpl(MethodImplOptions.NoInlining)]` members and type the field `object?`.
- **Use `ResolveTableName`.** `Database.TablePrefix` and `TableSuffix` exist so several servers can
  share one database; a hardcoded table name writes to the wrong one.

Core raises every API event on the game thread, so handlers must not block.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| Plugin refuses to start | `Database.Connection` names no entry | Fix the name, or add the entry to `configs/database.jsonc` |
| No points awarded | Fewer than `Rank.MinPlayers` on the server | Lower it, or wait for more players |
| No points during warmup | `Rank.WarmupPoints` is `false` | Set it to `true` — it discards the whole death, not just the points |
| Bot kills not counted | `Rank.PointsForBots` is `false` | Set it to `true` — it discards the whole death, not just the points |
| Everyone sits in the bottom tier | Ladder thresholds do not match the scoring pace | Re-tune `ranks.jsonc`, or use an example ladder |
| Scoreboard shows no rank | `Scoreboard.RankMode` is `Off` | Pick a mode; `Competitive` also needs an 18-band ladder |
| A module's tables are missing | The module loaded before core was ready | Check the console for its migration line at startup |

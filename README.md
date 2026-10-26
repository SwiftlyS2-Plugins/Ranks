<div align="center">
  <h1><strong>Ranks</strong></h1>
  <p>Points, ranks and lifetime statistics for CS2 — with a module API other plugins build on.</p>
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
- 📊 **Lifetime stats** - kills, deaths, assists, round and match results, bomb plants and defuses
- 🎖️ **Native scoreboard ranks** - Premier, Competitive, Wingman or Danger Zone, plus clan tags
- 🗄️ **MySQL, PostgreSQL or SQLite** - one connection, shared with every module
- 🧩 **Module API** - other plugins read ranks, react to changes, and own tables core migrates for them
- 🔁 **Live config reload** - prices, gates and multipliers apply on save
- 🏷️ **Multi-server friendly** - table prefixes and suffixes let several servers share one database

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

> ⚠️ **An unknown `Database.Connection` is a hard failure, deliberately.** SwiftlyS2 would
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

Four files, all created on first load, all reloaded on save.

| File | Holds |
|------|-------|
| `config.jsonc` | Database, gates, scoreboard, multipliers, general behaviour |
| `points.jsonc` | What each event is worth |
| `ladder.jsonc` | The rank tiers |
| `commands.jsonc` | Command names, aliases and permissions |

### `config.jsonc`

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
| `Rank.WarmupPoints` | bool | `false` | Award points during warmup |
| `Rank.PointsForBots` | bool | `false` | Count kills, deaths and assists involving bots |
| `Rank.FfaMode` | bool | `false` | Suppress team-damage penalties on free-for-all servers |

> **`PointsForBots` is not like the other gates.** `MinPlayers`, `WarmupPoints` and `FfaMode`
> suppress the *award* and still record the kill — those happened between real players. With
> `PointsForBots` off, bot kills, deaths and assists are **not recorded at all**, because a bot is
> not a player and counting it makes `!top kills` farmable against AI.

#### Scoreboard

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Scoreboard.RankMode` | string | `"Premier"` | `Off`, `Premier`, `Competitive`, `Wingman` or `DangerZone` |
| `Scoreboard.ScoreSync` | bool | `false` | Write points into the scoreboard score column |
| `Scoreboard.Clantags` | bool | `true` | Show the rank tag as a clan tag |
| `Scoreboard.SkillGroupOffset` | int | `0` | Shifts the icon; **Competitive mode only** |

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

#### VIP and General

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Vip.Multiplier` | double | `1.25` | Applied to every core award for flag holders |
| `Vip.Flags` | array | `["@ranks/vip"]` | Flags that earn the multiplier |
| `General.DetailedLogging` | bool | `false` | Narrate expected flow; warnings and errors always print |
| `General.KillStreaksEnabled` | bool | `true` | Award killstreak bonuses |
| `General.RoundEndSummary` | bool | `false` | Batch point messages into one round-end line |
| `General.ShowPointMessages` | bool | `true` | Server default for per-point chat messages |
| `General.ShowRankChangeMessages` | bool | `true` | Announce promotions and demotions |

### `points.jsonc`

One entry per event — 33 of them, plus eleven killstreak tiers. A value of `0` disables the event
and its chat line, but **not** its stat increment.

### `ladder.jsonc`

The rank tiers, each with a name, tag, colour and point threshold. The first tier must start at
`0`. Ships as a seven-band Premier ladder; an 18-band Competitive one is in
`resources/examples/`.

---

## How It Works

**Overview**

Every scored event runs through one pipeline: gates, base value, occurrence count, multipliers,
a single rounding at the end, then the write and the chat line.

**Rounding happens once, away from zero.** Rounding per stage lets a small award decay to nothing
through the multipliers, and a death penalty that rounds to `0` is a free death.

**Points are a judgement, counters are facts.** A disabled event, a context gate, or
`UseCoreRankingLogic = false` all stop the *award* while the stat counters keep running. The one
exception is `PointsForBots`, which stops both — see above.

**Persistence**

Records live in memory while a player is connected and are written every `SaveIntervalSeconds`,
on disconnect, and on map change. The in-memory record is authoritative, so core writes absolute
values rather than deltas.

**Modules**

Core publishes `IRanksApi` as a SwiftlyS2 shared interface. A module can read points and stats,
subscribe to point and rank changes, share core's database connection, and hand core its own
migrations to run. Modules own their tables; core only runs the migrations.

Two kinds exist. An **additive** module adds points alongside core's scoring. An **absolute**
module replaces it entirely — set `Rank.UseCoreRankingLogic` to `false` and the module becomes the
only thing writing points, while core keeps persistence, the ladder, the scoreboard and the
commands. At most one absolute module may run at a time; core does not police this.

### Official Modules

| Module | Kind | What it adds |
|--------|------|--------------|
| [Ranks_WeaponStats](Modules/Ranks_WeaponStats) | data | Per-weapon and per-hitbox statistics, with `!weaponstats` and `!hitstats` |
| [Ranks_EloPoints](Modules/Ranks_EloPoints) | absolute | Faceit-style Elo rating in place of per-kill scoring |

Modules that publish a read API of their own do so through `Ranks.Modules.Contract`, which ships
alongside `Ranks.Contract` in `resources/exports/`.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| Plugin refuses to start | `Database.Connection` names no entry | Fix the name, or add the entry to `configs/database.jsonc` |
| No points awarded | Fewer than `Rank.MinPlayers` on the server | Lower it, or wait for more players |
| No points during warmup | `Rank.WarmupPoints` is `false` | Set it to `true` |
| Bot kills not counted | `Rank.PointsForBots` is `false` | Set it to `true` — it gates the counters, not just the award |
| Everyone sits in the bottom tier | Ladder thresholds do not match the scoring pace | Re-tune `ladder.jsonc`, or use an example ladder |
| Scoreboard shows no rank | `Scoreboard.RankMode` is `Off` | Pick a mode; `Competitive` also needs an 18-band ladder |
| A module's tables are missing | The module loaded before core was ready | Check the console for its migration line at startup |

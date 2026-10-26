# Ranks_WeaponStats

Per-weapon and per-hitbox statistics for the [Ranks](../../README.md) plugin.

---

## Features

- 🔫 **Per-weapon totals** - shots, hits, damage, kills, deaths and headshots for every gun
- 🎯 **Hitbox breakdown** - where your bullets actually land, by hitgroup
- 📊 **In-game command** - `!weaponstats`, configurable name and permission
- 🤖 **Bot and friendly-fire switches** - count them or not, per game mode
- 🔌 **Published API** - other plugins read the same totals

**It awards no points and does not affect ranks.** Installing or removing it leaves
core's ladder untouched.

---

## Requirements

- `Ranks` core installed and loaded
- A SwiftlyS2 database connection — the same one core uses, named in **core's**
  `config.jsonc`, not here

The module creates its own tables on that connection and never touches core's.

---

## Installation

1. Drop the module into your plugins directory alongside `Ranks`.
2. Start the server once. Core creates the tables on first load.
3. Check the console for `Ranks_WeaponStats` — a missing `Ranks` is reported there,
   and the module stays idle rather than failing the server.

---

## Commands

### Player Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!weaponstats` | `!ws` | None | Your per-weapon table, best weapon first |

Name, aliases and permission are editable in `commands.jsonc`. An empty `Command`
removes the command. It is module-owned and does not appear in core's `!rank` menu.

> [!NOTE]
> **Hitgroup totals have no command.** They are collected and stored, and reachable
> through `GetHitGroupsAsync` on the published API, but nothing in this module prints
> them in chat.

---

## Configuration

Config file: `addons/swiftlys2/plugins/Ranks_WeaponStats/config.jsonc`

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Tracking.CountFriendlyFire` | bool | `false` | Count hits, damage, hitgroups and kills against players on your own team |
| `Tracking.CountAgainstBots` | bool | `false` | Count hits, damage, kills, headshots and hitgroups when the **victim** is a bot |
| `Tracking.CountFromBots` | bool | `false` | Count deaths when the **killer** is a bot |
| `Database.FlushIntervalSeconds` | int | `120` | How often counters are written; `0` flushes only on disconnect, map change and unload |
| `Display.TopWeapons` | int | `10` | Rows in the `!weaponstats` list |
| `Display.MinShotsToList` | int | `10` | Hides weapons barely touched, so one stray pistol shot does not fill the table |
| `General.DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors print regardless |

### Example Configuration

```jsonc
{
  "WeaponStats": {
    "Tracking": {
      "CountFriendlyFire": false,
      "CountAgainstBots": false,
      "CountFromBots": false
    },
    "Database": { "FlushIntervalSeconds": 120 },
    "Display": { "TopWeapons": 10, "MinShotsToList": 10 },
    "General": { "DetailedLogging": false }
  }
}
```

### Friendly fire — match this to your game mode

**On a normal team server leave it `false`.** Shooting a teammate is not combat
performance, and counting it lets someone pad damage and accuracy on their own side.

**On an FFA or deathmatch server set it `true`.** Everyone is typically on one team
there, so with it `false` the module sees nearly every kill as friendly fire and
records **almost nothing**. Core has the matching switch in `Rank.FfaMode`; set both
the same way.

> [!NOTE]
> The module warns once at startup if core reports FFA mode while this is still
> `false` — the symptom otherwise looks like a broken plugin rather than a setting.

**Self damage is never counted** under any mode, and **deaths are never filtered by
this switch**: being killed by a teammate is still a death.

### The two bot switches

Independent on purpose — set either, both or neither. The usual reason to split them
is a practice server where bot kills are padding but deaths to bots still matter.

**Both default to `false`, matching core's `Rank.PointsForBots`.** At the shipped
defaults this module's bot handling matches core's, so on a server without warmup the
totals reconcile:

```
SUM(ranks_weapon_stats.kills) per player  ==  ranks_players.kills
```

Setting either switch independently of core's breaks that, and `!weaponstats` will
disagree with `!stats`. That is an operator choice rather than a bug — but worth
knowing before the first player reports it.

> [!NOTE]
> **Warmup is a second, unavoidable source of divergence.** Core drops warmup deaths
> entirely when its `Rank.WarmupPoints` is off; this module has no warmup switch and
> records them, so its totals run ahead on any server with warmup enabled. There is no
> setting that reconciles the two today.

> [!WARNING]
> **`CountAgainstBots = false` makes accuracy read low.** Shots come from
> `weapon_fire`, which has no victim and therefore cannot be filtered — shots at bots
> count while the resulting hits do not. Nothing can fix this; the event carries no
> target. Turn both switches **on** (alongside core's `PointsForBots`) if you run
> bots and care about accuracy.

Neither switch affects bots' *own* rows: a bot never gets stats of its own, because
it has no SteamID64 to key them to.

---

## How It Works

**Overview**

Every shot, hit and death is accumulated in memory per player, then written to the
database in batches.

**Collection**

`weapon_fire` increments shots, `player_hurt` adds hits, damage and the hitgroup, and
`player_death` records kills, headshots and deaths. The tracking switches decide which
of those count; the shot counter is the one that cannot be filtered, since the fire
event names no victim.

**Persistence**

Counters flush on the timer, on disconnect, on map change and on unload. Writes are
**additive** rather than absolute, so two servers sharing one database accumulate
instead of overwriting each other, and nothing has to be read back when a player
connects.

**Reading**

`!weaponstats` and the API both merge the stored rows with the caller's unflushed
session delta, so a figure never lags a flush interval behind what just happened.
`Display.TopWeapons` and `Display.MinShotsToList` shape the chat table only; the API
returns every row.

---

## For plugin developers

The module publishes `Ranks.WeaponStats.Api.v1`. Reference
`Ranks_WeaponStats.Contract.dll` from this module's `resources/exports/` — **do not
copy it next to your plugin**, or the host loads a second one, the interface gets two
type identities, and binding silently stops matching.

```csharp
public interface IWeaponStatsApi
{
    const string InterfaceKey = "Ranks.WeaponStats.Api.v1";

    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId);
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId);
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon);
}
```

`Weapon` is the entity designer name (`weapon_ak47`, not `ak47`); `GetWeaponAsync`
normalises either spelling. `HitGroup` is the raw `HitGroup_t` value, which is not a
dense range — `0` through `11` with a hole at `9`.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| "this module is idle" at startup | Ranks is not installed | Install Ranks alongside it |
| Almost nothing is recorded | FFA server with `CountFriendlyFire` off | Set it `true`, and core's `Rank.FfaMode` to match |
| Accuracy looks far too low | Bots on the server with `CountAgainstBots` off | Turn both bot switches on, with core's `PointsForBots` |
| `!weaponstats` disagrees with `!stats` | Bot switches differ from core's `PointsForBots` | Set all three the same way |
| Stats lost after a crash | Flush interval too long | Lower `Database.FlushIntervalSeconds` |
| No tables created | Core could not reach the database | Check core's connection name and the startup log |
| `!hitstats` does nothing | It does not exist | Hitgroup totals are API-only; see the note under Commands |

# Ranks_WeaponStats

Per-weapon and per-hitbox statistics for the [Ranks](../../README.md) plugin.

Tracks what each player does with each weapon — shots, hits, damage, kills,
deaths, headshots — and where their bullets land, broken down by hitgroup.

**It awards no points and does not affect ranks.** Installing or removing it
leaves core's ladder untouched.

---

## Requires

- `Ranks` core installed and loaded
- A SwiftlyS2 database connection — the same one core uses, named in **core's**
  `config.jsonc`, not here

The module creates its own tables on that connection and never touches core's.

---

## Install

1. Drop the module into your plugins directory alongside `Ranks`.
2. Start the server once. Core creates the tables on first load.
3. Check the console for `Ranks_WeaponStats` — a missing `Ranks` is reported
   there, and the module stays idle rather than failing the server.

---

## Commands

| Command | Aliases | Shows |
|---|---|---|
| `!weaponstats` | `!ws` | your per-weapon table, best weapon first |
| `!hitstats` | `!hs` | where your shots land, by hitgroup |

Names, aliases and permissions are configurable — see below. Both are
module-owned; they do not appear in core's `!rank` menu.

---

## Configuration

Two files, both created on first load.

### `config.jsonc`

| Key | Default | Meaning |
|---|---|---|
| `Tracking.CountFriendlyFire` | `false` | Count hits, damage, hitgroups and kills against **players on your own team**. |
| `Tracking.CountAgainstBots` | `false` | Count hits, damage, kills, headshots and hitgroups when the **victim** is a bot. Match this to core's `Rank.PointsForBots`. |
| `Tracking.CountFromBots` | `false` | Count deaths when the **killer** is a bot. Match this to core's `Rank.PointsForBots`. |
| `Database.FlushIntervalSeconds` | `120` | How often accumulated counters are written. Lower loses less on a crash and writes more often. `0` disables the timer — counters then flush only on disconnect, map change and unload. |
| `Display.TopWeapons` | `10` | Rows in the `!weaponstats` list. |
| `Display.MinShotsToList` | `10` | Hides weapons you have barely touched, so one stray pistol shot does not fill the table. |
| `General.DetailedLogging` | `false` | Narrates expected flow. Warnings and errors print regardless. |

#### Friendly fire — set this to match your game mode

**On a normal team server leave it `false`.** Shooting a teammate is not combat
performance, and counting it lets someone pad their damage and accuracy on
their own side.

**On an FFA or deathmatch server set it `true`.** Everyone is typically on one
team there, so with it `false` the module sees nearly every kill as friendly
fire and records **almost nothing**. Core has the matching switch in
`Rank.FfaMode`; set both the same way.

> The module warns once at startup if core reports FFA mode while this is still
> `false`, because the symptom otherwise looks like a broken plugin rather than
> a setting.

**Self damage is never counted** — your own grenade or molotov on yourself —
and that is not configurable under any mode. **Deaths are never filtered by
this switch either**: being killed by a teammate is still a death, and it still
counts.

#### The two bot switches

They are independent on purpose — set either, both or neither. The usual reason
to split them is a practice or deathmatch server where bot kills are padding but
deaths to bots are still worth knowing about, or the reverse.

**Both default to `false`, matching core's `Rank.PointsForBots`.** Core gates bot
kills, deaths and assists out of `ranks_players` entirely (FR-PTS-03) — a bot is
not a player, so a kill on one is not a fact about play. At the shipped defaults
this module's totals therefore reconcile with core's:

```
SUM(ranks_weapon_stats.kills) per player  ==  ranks_players.kills
```

**Setting either switch independently of core's breaks that.** `!weaponstats`
will then disagree with `!stats`, and that is the operator's choice rather than
a bug — but it is worth knowing before the first player reports it.

> ⚠️ **`CountAgainstBots = false` makes accuracy read low.** Shots come from
> `weapon_fire`, which has no victim at all, so they cannot be filtered — shots
> at bots still count while the resulting hits do not. On a bot-heavy server the
> hits-per-shot ratio becomes meaningless. Nothing can fix this; the event does
> not carry a target. Turn both switches **on** (alongside core's
> `PointsForBots`) if you run bots and care about accuracy.

Neither switch affects bots' *own* rows: a bot never gets stats of its own under
any setting, because it has no SteamID64 to key them to (FR-DB-07).

### `commands.jsonc`

Same shape as core's. Each command takes a name, aliases and a permission:

```jsonc
{
  "weaponstats": { "name": "weaponstats", "aliases": ["ws"], "permission": "" },
  "hitstats":    { "name": "hitstats",    "aliases": ["hs"], "permission": "" }
}
```

An empty `permission` means everyone. **Renames take effect on restart**, not on
file reload.

> Check names against your other plugins before changing them. The first plugin
> to register a command wins silently, and the loser simply never fires.

---

## What gets stored

Two tables, carrying whatever prefix and suffix core is configured with:

| Table | Key | Holds |
|---|---|---|
| `ranks_weapon_stats` | `(steam_id, weapon)` | shots, hits, damage, kills, deaths, headshots |
| `ranks_hit_stats` | `(steam_id, hitgroup)` | hits and damage you have **dealt** to each hitbox |

`weapon` holds the entity designer name — `weapon_ak47`, not `ak47` — so it
joins directly against CS2's own weapon names. Non-weapon death sources keep
theirs: `world`, `planted_c4`, `inferno`, `trigger_hurt`.

Bots are never stored, matching core.

**Friendly fire and self damage are never counted** — not hits, damage,
hitgroups or kills. Deaths are the exception and count whatever killed you,
including a teammate. Damage is health only; armour absorbed is not recorded.
On an FFA server the friendly-fire exclusion switches itself off, or it would
hide every kill.

Rows are removed when core purges a player (FR-DB-08), so the module's data
follows core's retention policy rather than growing forever on its own.

---

## Notes for operators

- **Counters are written additively.** Two servers sharing one database
  accumulate into the same rows instead of overwriting each other — but that
  also means they share one set of totals. Use core's table prefix to separate
  them if you want per-server stats.
- **The bot switches change what is recorded, not what is displayed.** Turning
  one off stops new rows accruing; it does not hide or remove what was already
  counted while it was on.
- **Shot counts depend on a game event Valve may change.** `weapon_fire` lives
  only in `mod.gameevents`. If a CS2 update stops firing it, shots and accuracy
  go flat while kills and damage keep working.

# Ranks_EloPoints

Faceit-style Elo rating for the [Ranks](../../README.md) plugin.

Rating moves on **match outcome relative to expectation**, not on kills. Beating
a stronger team gains more than beating a weaker one; losing to a stronger one
costs less. A player who never fires a shot but wins every round still climbs.

**This is an absolute module** (FR-PTS-00). While installed it is the sole
scoring authority, and core stops scoring entirely. At most one absolute module
may be active at a time.

---

## Requires

- `Ranks` core installed and loaded
- **`Rank.UseCoreRankingLogic` set to `false` in core's `config.jsonc`**
- A SwiftlyS2 database connection — the same one core uses, named in **core's**
  config, not here
- A competitive match configuration (`mp_maxrounds`, halftime, win conditions)

> ⚠️ **Leaving `UseCoreRankingLogic` on is the one misconfiguration that
> silently produces nonsense.** Core keeps awarding per kill while this module
> overwrites the same value with a rating, so the number means neither thing.
> The module warns once at startup when it sees that combination — it warns
> rather than overriding, because core's config is the operator's to set.

### Scope

**Built for competitive 5v5 and nothing else.** It assumes two teams, a round
structure, and a match that ends. On FFA, deathmatch, arenas or surf it will
produce numbers, and those numbers will be meaningless. Matching players into
sensible teams is the server operator's job — this module rates the result, it
does not decide who may play.

---

## Install

1. Set `Rank.UseCoreRankingLogic` to `false` in **core's** `config.jsonc`.
2. Drop the module into your plugins directory alongside `Ranks`.
3. Start the server once. Core creates the table on first load.
4. Check the console for `Ranks_EloPoints` — a missing `Ranks` is reported
   there, and the module stays idle rather than failing the server.
5. Replace core's rank ladder with a rating-shaped one. A 10-band Faceit-style
   ladder ships at `resources/examples/ranks-elo.jsonc`; core's default ladder
   is built for accumulating points and will put everyone in the bottom tier.

---

## Commands

**None.** Unlike `Ranks_WeaponStats`, this module ships no commands of its own.

It writes the rounded rating into core's `points`, so `!rank`, `!top`, the
scoreboard and the clan tag all show it already. Adding a `!elo` would be a
second way to read the same number.

---

## Configuration

One file, `config.jsonc`, created on first load.

| Key | Default | Meaning |
|---|---|---|
| `Rating.Mode` | `RoundAccumulation` | `RoundAccumulation` scores every round and averages; `Classic` scores the match result once. See below. |
| `Rating.K` | `50` | Maximum swing between two evenly matched teams. 50 gives ±25 on a clean win or loss, which is Faceit's feel. |
| `Rating.PerformanceBonus` | `10` | Maximum swing from individual performance within the match. `0` makes rating depend on the result alone. |
| `Rating.ConfidenceRounds` | `10` | How many rounds of evidence a result is worth before it is believed in full. Higher damps short sessions harder. `0` disables shrinkage. |
| `Rating.ExpectedRounds` | `24` | Fallback match length, used only when `mp_maxrounds` cannot be read. |
| `Abandon.Penalty` | `25` | Deducted **on top of** the match result when a player leaves and never returns. `0` disables. Keep it at or above `K / 2` — see below. |
| `General.DetailedLogging` | `false` | Narrates expected flow. Warnings and errors print regardless. |

### What is read from core instead of configured here

Four settings already exist in core's config, and FR-MOD-05 rule 3 says a rating
module reads them rather than making the operator set the same thing twice:

| Core key | Used as |
|---|---|
| `Rank.StartPoints` | the rating an unrated player enters their first match with |
| `Rank.MinPoints` | the floor a rating cannot fall below |
| `Rank.MinPlayers` | the minimum humans for a match to be rated at all |
| `Rank.WarmupPoints` | whether warmup rounds count |
| `Rank.PointsForBots` | whether a team with no rated humans can be rated |

Core applies none of these itself with `UseCoreRankingLogic` off. They are still
the operator's stated intent, and this module honours them.

### The two modes

**`RoundAccumulation` (default)** scores each round against the expectation at
that moment, then averages across the match. A player who joins at round 18 is
rated on the four rounds they played, not on a result they had almost no part
in.

**`Classic`** is textbook Elo: one win/draw/loss against one expectation, scaled
by how much of the match the player was present for. Simpler, and much blunter
about substitutes.

Both run through the same code path and the same clamps; they differ only in how
the actual and expected scores are produced.

### Tuning `K` and `ConfidenceRounds` together

They pull in opposite directions and are easy to set contradictorily.

`K` sets the ceiling. `ConfidenceRounds` decides how much of that ceiling a
given result actually earns — a 13-0 stomp between even teams is worth `+25`
unshrunk, but `+14.1` at the shipped `ConfidenceRounds = 10`.

If ratings feel sluggish, **raise `K` before lowering `ConfidenceRounds`**.
Lowering confidence makes short sessions swing as hard as full matches, which is
the thing it exists to prevent.

---

## The abandon penalty

A player who disconnects, **or moves to the spectator team**, and is still absent
when the match ends takes `Abandon.Penalty` on top of whatever the match itself
cost or paid them.

Coming back clears it. The penalty is decided at match end, not when they leave,
so a reconnect inside the same match is invisible to the rating.

Three consequences worth knowing before the first bug report:

- **Going to spectator counts as leaving.** Without that, the correct play when
  losing would be to spectate, and the ladder would be worthless.
- **AFK managers move idle players to spectator.** On a server running one, an
  AFK player is abandoning by this definition and will be penalised.
- **A late filler pays the same 25 as a full-match abandoner.** Someone who
  joins at round 17 and leaves at round 19 earns about `+0.4` and loses 25.
  That mildly discourages filling an empty slot; set `Abandon.Penalty` to `0` if
  that matters more on your server than deterring quitters.

### How large the penalty needs to be

Leaving early accrues almost no loss, so the penalty has to cover everything
staying would still have cost. The threshold that always deters is **`K × E`**.

The shipped `25` is `K / 2`, which fully covers **balanced** matches — `E = 0.5`
caps the loss there at 25. On a lopsided match a determined quitter can still
beat it by leaving in the first third; closing that needs roughly `K × 0.9`
(`45` at the defaults), which is harsh on genuine disconnects everywhere else.

`docs/verify-figures.ps1` reproduces the full table for whatever values you are
considering.

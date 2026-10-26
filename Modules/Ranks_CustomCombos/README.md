<div align="center">
  <h1><strong>Ranks CustomCombos</strong></h1>
  <p>Operator-defined kill combinations for the Ranks plugin.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/SwiftlyS2-1.5.1--beta.12-blue?style=flat" alt="SwiftlyS2 Version">
  <img src="https://img.shields.io/badge/requires-Ranks-informational?style=flat" alt="Requires Ranks">
</p>

---

## Features

- 🎯 **Combine attributes** - "a no-scope headshot is worth 10", "a knife headshot is worth 50"
- 💥 **Same-tick multi-kills** - one grenade killing three players
- 🥇 **Closest match wins** - escalating tiers pay the most specific, never the sum
- ➕ **Additive, or replacing** - add a bonus on top, or take the kill over entirely
- 📊 **Per-combo counters** - `!combos` shows what a player has landed
- 🔁 **Live reload** - edit the file and the next kill uses it

---

## Requirements

- SwiftlyS2
- The **Ranks** plugin. Without it this module is idle: no awards, no tables.

> ⚠️ **Do not enable `SuppressCoreAwards` alongside an absolute rating module**
> such as `Ranks_EloPoints`. Both would be deciding the same number, and nothing
> detects it. Additive mode (the default) is safe with either.

---

## Installation

1. Drop the plugin folder into `addons/swiftlys2/plugins/`.
2. Restart. `config.jsonc` and `commands.jsonc` are created, and core runs the migration.
3. **No combos ship by default.** Copy what you want from
   `resources/examples/combos.jsonc` into the `Rules` object — what a combination is
   worth is a balance decision, not a default.

---

## Attributes

A combo lists attributes that must **all** hold on one kill. Combos are custom; the
attribute vocabulary is not.

### Flags

Named for the `player_death` field they read, so a name can be checked against an event
dump rather than against this table.

| Attribute | True when |
|---|---|
| `headshot` | the kill was a headshot |
| `noscope` | fired without scoping |
| `thrusmoke` | the bullet passed through smoke |
| `attackerblind` | the attacker was flashed |
| `attackerinair` | the attacker's feet were off the ground |
| `assistedflash` | someone else's flash set the kill up |
| `penetrated` | the bullet went through a surface — a wallbang |
| `dominated` | the kill started a domination |
| `revenge` | the kill was revenge on a dominator |
| `team_kill` | attacker and victim on the same team |

Four of these — `attackerinair`, `assistedflash`, `dominated`, `revenge` — **core does
not price at all**, so a one-attribute combo on them is the only way to score them.

### Valued

These take a value after a colon, because the field is a number rather than a flag.

| Attribute | Matches |
|---|---|
| `hitgroup:<n>` | that exact hitgroup — `1` head, `2` chest, `3` stomach, `4`/`5` arms, `6`/`7` legs |
| `weapon:<classname>` | that exact weapon, e.g. `weapon:weapon_ak47` |
| `distance:<units>` | at or past that many source units — `2000` is roughly 50 m |

### Derived

| Attribute | True when |
|---|---|
| `weapon_class:<class>` | one of `knife` `grenade` `inferno` `taser` `pistol` `smg` `rifle` `sniper` `shotgun` `heavy` |
| `first_blood` | the first kill of the round |

An unknown attribute is reported at load with the combo named and skipped, rather than
quietly never matching.

---

## Configuration

`config.jsonc`, section `Combos`.

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `SuppressCoreAwards` | bool | `false` | Matched combo replaces core's kill award instead of adding to it |
| `Rules` | object | `{}` | The combos, keyed by name |
| `Database.FlushIntervalSeconds` | int | `120` | How often counts are written. Minimum 15 |
| `General.DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors always print |

Per combo:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `RequiresAll` | array | — | Every attribute must be present. Required |
| `RequiresNone` | array | `[]` | None of these may be present |
| `Count` | int | `1` | Matching kills needed **on the same tick** |
| `Points` | int | — | What it pays. Required, non-zero |
| `Message` | string | `combo.<key>` | Translation key for the chat line |

### Example

```jsonc
"Combos": {
  "SuppressCoreAwards": false,
  "Rules": {
    "noscope_hs": {
      "RequiresAll": [ "headshot", "noscope" ],
      "Points": 10
    },
    "nade_double": {
      "RequiresAll": [ "weapon_class:grenade" ],
      "Count": 2,
      "Points": 75
    }
  }
}
```

Add a `combo.<key>` entry to `resources/translations/en.jsonc` for each combo. A missing
key falls back to the bare combo name rather than printing a `{lang}.{key}` marker.

---

## Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!combos` | `!mycombos` | None | The combos you have landed, highest first |

---

## How It Works

**Core asks before it awards.** Core computes a kill's attributes once and consults this
module; the module answers with a claim, and core awards it through its own pipeline. That
ordering is what makes `SuppressCoreAwards` possible at all — a plugin watching
`player_death` on its own cannot stop core, and plugin order is not guaranteed.

It also means a claim gets core's gates, the VIP multiplier, one rounding, the per-player
`!togglepointmsg` preference and round-end batching for free, rather than this module
reimplementing five things and drifting.

**Closest match wins.** Several combos can match one kill; the one with the most
`RequiresAll` entries pays, and ties go to config order. Given `[headshot, noscope] = 10`
and `[headshot, noscope, penetrated] = 25`, a no-scope wallbang headshot pays **25, not 35**.

**`Count` is same tick only.** It is for one grenade killing three players, not a streak.
Streaks and timed multi-kills are core's, which already has eleven tiers and a configurable
window. `Count` above 1 cannot be combined with `SuppressCoreAwards` — core asks per kill,
so the first two kills of a three-kill run are already paid by the time the third arrives,
and suppressing only the last would be unpredictable. It is rejected at load.

**Counters are facts.** A combo is counted whenever it matches, including when a gate
refuses the points. The one exception is a bot victim with core's `PointsForBots` off,
which suppresses the counter too — matching what core does for kills.

---

## For plugin developers

The module publishes `Ranks.CustomCombos.Api.v1`. Reference
`Ranks_CustomCombos.Contract.dll` from this module's `resources/exports/` — **do not copy
it next to your plugin**, or the host loads a second one and the interface gets two type
identities, at which point binding silently stops matching.

```csharp
public interface ICustomCombosApi
{
    const string InterfaceKey = "Ranks.CustomCombos.Api.v1";

    Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId);
    Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey);
    IReadOnlyList<string> ConfiguredCombos { get; }
}

public sealed record ComboStatRow(string ComboKey, long Count, DateTime LastAt);
```

Both async methods include the player's **unflushed** counts, so a figure read here matches
what `!combos` prints rather than lagging a flush interval behind.

`ConfiguredCombos` is synchronous because it reads the loaded rule set rather than the
database. It is the only way to render a combo an operator has configured but nobody has
landed yet — no counter row exists for those.

Combo keys are operator-defined, so the set is server-specific: treat an unfamiliar key as
ordinary data, not an error.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| Nothing is awarded | No combos configured | Copy from `resources/examples/combos.jsonc` |
| "this module is idle" at startup | Ranks is not installed | Install Ranks |
| A combo was skipped at load | Unknown attribute, empty `RequiresAll`, zero `Points`, or `Count` with suppression | The log names the combo and the reason |
| Chat shows `noscope_hs` instead of words | No `combo.noscope_hs` translation | Add it to `resources/translations/en.jsonc` |
| A combo never fires | A more specific combo is winning | Check for an overlapping combo with more attributes |
| Points look doubled | Additive mode is intended — the combo adds to core's kill award | Enable `SuppressCoreAwards` to replace instead |

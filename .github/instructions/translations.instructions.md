---
description: 'Translation files and the localizer: key completeness, dynamic key families, chat colour tags, placeholders. Trigger phrases: translation, localization, localizer, language, jsonc keys, chat colours.'
applyTo: '**/translations/*.jsonc'
---

# Translations / Localization

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

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


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

> [!NOTE]
> **Coming from another ranking plugin?** A migration tool for **K4-LevelRanks** and other
> popular ranking plugins is in development and will be shared shortly — points, stats and
> per-weapon history carried across in one step. Keep your old database until then.

---

## ✨ Features

- 🏆 **Points and ranks** — a configurable ladder, priced per event, with VIP and dynamic multipliers
- 📊 **Gameplay stats** — kills, deaths, assists, round and match results, bomb plants and defuses
- 🎖️ **Native scoreboard ranks** — Premier, Competitive, Wingman, or your own icon set, plus rank clan tags
- 🗄️ **MySQL, MariaDB, PostgreSQL or SQLite** — one connection, shared with every module
- 🧩 **Module API** — other plugins read ranks, react to changes, and own tables core migrates for them
- 🔁 **Live config reload** — points, ladder and scoreboard apply on save; only command names and table affixes need a restart
- 🏷️ **Multi-server friendly** — table prefixes and suffixes let several servers share one database

## 🚀 Getting Started

| Guide | Description |
|-------|-------------|
| **[Features](../../wiki/Features)** | The full list, and what core deliberately does not do |
| **[Installation](../../wiki/Installation)** | Install, pick a database, verify |
| **[Commands](../../wiki/Commands)** | Player and admin commands |
| **[Menus](../../wiki/Menus)** | The one menu tree, and what each category holds |
| **[Configuration](../../wiki/Configuration)** | Every setting, with defaults |

## ⚙️ Tuning Rank Progression

| Topic | Description |
|-------|-------------|
| **[Points and Events](../../wiki/Points-And-Events)** | Price every action, and the gates that suppress them |
| **[Rank Ladder](../../wiki/Rank-Ladder)** | Turn points into named tiers |
| **[Scoreboard and Rank Icons](../../wiki/Scoreboard-And-Rank-Icons)** | Five modes, plus your own icon set |
| **[Database](../../wiki/Database)** | Shared ladders, seasons, purging, persistence |

## 🧩 Modules

Four official modules ship alongside core. Adding one touches nothing in core.

| Module | Kind | What it adds |
|--------|------|--------------|
| **[Ranks_WeaponStats](../../wiki/Module-WeaponStats)** | data | Per-weapon and per-hitbox statistics, with `!weaponstats` |
| **[Ranks_EloPoints](../../wiki/Module-EloPoints)** | absolute | Faceit-style Elo rating in place of per-kill scoring |
| **[Ranks_CustomCombos](../../wiki/Module-CustomCombos)** | additive | Operator-defined kill combinations, with `!combos` |
| **[Ranks_Prestige](../../wiki/Module-Prestige)** | display | Prestige levels as a virtual point deduction, with `!prestige` |

---

## 📚 Reference

| Topic | Description |
|-------|-------------|
| **[API Reference](../../wiki/API-Reference)** | Every member of `IRanksApi` |
| **[API Integration](../../wiki/API-Integration)** | Build your own module |
| **[Building](../../wiki/Building)** | Build from source and release |
| **[Troubleshooting](../../wiki/Troubleshooting)** | Symptoms, causes and fixes |

---

## 🤝 Report issues or request features

- **Issues**: [GitHub Issues](https://github.com/shmitzas/Ranks/issues)

---

<div align="center">
<p>Made with ❤️ by Shmitzas</p>
<p>To play in my CS2 servers go to <a href="https://rampage.lt">Rampage.lt</a></p>
<p>⭐ Star this repository if you find it useful!</p>
</div>

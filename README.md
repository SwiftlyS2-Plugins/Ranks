<div align="center">
  <h1><strong>Ranks</strong></h1>
  <p>Points, ranks and gameplay statistics for CS2 — with a module API other plugins build on.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <a href="https://github.com/shmitzas/Ranks/releases/latest"><img src="https://img.shields.io/github/v/release/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Latest Release"></a>
  <img src="https://img.shields.io/github/downloads/shmitzas/Ranks/total?style=flat&logo=github&cacheSeconds=3600" alt="Downloads">
  <img src="https://img.shields.io/github/stars/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Stars">
</p>

> [!NOTE]
> **Migrating from another ranking plugin?**<br>
> [Ranks_Importer](https://github.com/shmitzas/PluginDataImporters)
> is a separate one-shot tool that imports from:
> - **[LVL Ranks](https://github.com/Pisex/cs2-lvl_ranks)** - by [Pisex](https://github.com/Pisex) — the original, plus its [modules](https://github.com/Pisex/cs2-lvl_ranks_modules)
> - **[K4-LevelRanks-SwiftlyS2](https://github.com/K4ryuu/K4-LevelRanks-SwiftlyS2)** - by [K4ryuu](https://github.com/K4ryuu) — now archived
> - **[K4-LevelRanks-Upkeep](https://github.com/shmitzas/K4-LevelRanks-Upkeep)** - by [shmitzas](https://github.com/shmitzas) — the maintained fork, with minimal changes to original fork
> - **[K4-System](https://github.com/karola3vax/K4-System)** - by [karola3vax](https://github.com/karola3vax) — a rewrite of K4ryuu's original
> - **[K4-Zenith](https://github.com/K4ryuu/K4-Zenith)** - by [K4ryuu](https://github.com/K4ryuu) — its Ranks, Stats and TimeStats modules

---

## ✨ Features

All included in Ranks. [Modules](#-modules) add more.

- 🏆 **Points and ranks** — points per event, a rank ladder, killstreaks, VIP and dynamic multipliers
- 📊 **Gameplay stats** — kills, deaths, assists, headshots, accuracy, rounds, matches, bombs
- ⏱️ **Playtime** — `!mytime` and the `!ttop` leaderboard
- 🗓️ **Seasons** — optional monthly or every-N-days rank reset; past seasons stay browsable
- 🎖️ **Scoreboard ranks** — Premier, Competitive, Wingman or your own icons, plus clan tags
- 👀 **Everyone's rank visible** — all players see each other's rank icons
- 🗄️ **MySQL, MariaDB, PostgreSQL or SQLite** — one connection, shared with every module
- 🧩 **Module API** — other plugins read ranks, award points and add menu rows
- 🔁 **Live config reload** — most settings apply on save
- 🏷️ **Multi-server** — share one database, or keep a ladder per server

## 🚀 Getting Started

| Guide | Description |
|-------|-------------|
| **[Features](../../wiki/Features)** | Everything Ranks does |
| **[Installation](../../wiki/Installation)** | Install and verify |
| **[Commands](../../wiki/Commands)** | Player and admin commands |
| **[Menus](../../wiki/Menus)** | The `!rank` menu |
| **[Configuration](../../wiki/Configuration)** | Every setting, with defaults |

## ⚙️ Tuning Rank Progression

| Topic | Description |
|-------|-------------|
| **[Points and Events](../../wiki/Points-And-Events)** | Points per event and killstreak |
| **[Rank Ladder](../../wiki/Rank-Ladder)** | The ranks and their thresholds |
| **[Scoreboard and Rank Icons](../../wiki/Scoreboard-And-Rank-Icons)** | Icon modes and custom icon sets |
| **[Database](../../wiki/Database)** | Tables, multi-server, seasons, purging |

## 🧩 Modules

Official modules. **[Modules Overview](../../wiki/Modules)** covers installing them and which ones can run together.

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

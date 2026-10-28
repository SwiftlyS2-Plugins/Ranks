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

# :jigsaw: Ranks is a platform, not just a ranking plugin

### Core publishes `IRanksApi` as a SwiftlyS2 shared interface — see **[API Integration](../../wiki/API-Integration)** in the WIKI

A module can read points and stats, react to point and rank changes, claim a kill before core
prices it, override the scoreboard, and hand core its own migrations to run. Four official modules
ship alongside core, and writing your own touches nothing in core.

## ✨ Features

- 🏆 **Points and ranks** - a configurable rank ladder, priced per event, with VIP and dynamic multipliers
- 📊 **Gameplay stats** - kills, deaths, assists, round and match results, bomb plants and defuses
- 🎖️ **Native scoreboard ranks** - Premier, Competitive, Wingman, or a Custom mode for replacement icon sets, plus rank clan tags
- 🗄️ **MySQL, MariaDB, PostgreSQL or SQLite** - one connection, shared with every module
- 🧩 **Module API** - other plugins read ranks, react to changes, and own tables core migrates for them
- 🔁 **Live config reload** - points, rank ladder and scoreboard changes apply on save; only command names and table affixes need a restart
- 🏷️ **Multi-server friendly** - table prefixes and suffixes let several servers share one database

## 🚀 Getting Started

| Guide | Description |
|-------|-------------|
| **[Installation](../../wiki/Installation)** | Install, pick a database, verify |
| **[Commands](../../wiki/Commands)** | Player and admin commands |
| **[Configuration](../../wiki/Configuration)** | Every setting, with defaults |

## 🏆 Tuning Rank Progression

| Topic | Description |
|-------|-------------|
| **[Points and Events](../../wiki/Points-And-Events)** | Price every action, and the gates that suppress them |
| **[Rank Ladder](../../wiki/Rank-Ladder)** | Turn points into named tiers |
| **[Scoreboard and Rank Icons](../../wiki/Scoreboard-And-Rank-Icons)** | Five modes, plus your own icon set |
| **[Database](../../wiki/Database)** | Shared rank ladders, seasons, purging, persistence |

## 🧩 Modules

| Module | Kind | What it adds |
|--------|------|--------------|
| **[Ranks_WeaponStats](../../wiki/Module-WeaponStats)** | data | Per-weapon and per-hitbox statistics, with `!weaponstats` |
| **[Ranks_EloPoints](../../wiki/Module-EloPoints)** | absolute | Faceit-style Elo rating in place of per-kill scoring |
| **[Ranks_CustomCombos](../../wiki/Module-CustomCombos)** | additive | Operator-defined kill combinations, with `!combos` |
| **[Ranks_Prestige](../../wiki/Module-Prestige)** | display | Prestige levels as a virtual point deduction, with `!prestige` |

Only the two scoring kinds can conflict. An **additive** module adds points alongside core's
scoring. An **absolute** module replaces it entirely — set `Rank.UseCoreRankingLogic` to `false`
and the module becomes the only thing writing points, while core keeps persistence, the rank ladder,
the scoreboard and the commands.

> [!WARNING]
> **At most one absolute module may run at a time, and core does not police this.** Two of them
> overwrite each other's rating with no error anywhere.

A **data** module only records, and a **display** module only changes what the scoreboard shows;
neither writes points, so both compose with anything.

Modules that publish a read API of their own ship their own contract assembly, from their own
directory — adding one never touches core.

See **[Modules](../../wiki/Modules)** for which combinations are safe.

## 📚 Documentation

| Topic | Description |
|-------|-------------|
| **[Features](../../wiki/Features)** | The full list, and what core deliberately does not do |
| **[API Reference](../../wiki/API-Reference)** | Every member of `IRanksApi` |
| **[API Integration](../../wiki/API-Integration)** | Build a module |
| **[Building](../../wiki/Building)** | Build from source and release |
| **[Troubleshooting](../../wiki/Troubleshooting)** | Symptoms, causes and fixes |

---

<div align="center">
  <h3>💖 Support Development</h3>
  <p><em>This plugin is and will remain completely free! If you find it useful and want to support development, tips are always appreciated &lt;3</em></p>
  <a href='https://ko-fi.com/J3J21Q4PP2' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>
</div>

---

## 🤝 Report issues or request features

- **Issues**: [GitHub Issues](https://github.com/shmitzas/Ranks/issues)

---

<div align="center">
<p>Made with ❤️ by Shmitzas</p>
<p>To play in my CS2 servers go to <a href="https://rampage.lt">Rampage.lt</a></p>
<p>⭐ Star this repository if you find it useful!</p>
</div>

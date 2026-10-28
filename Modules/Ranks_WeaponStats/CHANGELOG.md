# Changelog

Notable changes to Ranks_WeaponStats, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_WeaponStats.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.0.0] - 2026-10-26

First release. Per-weapon and per-hitbox statistics for the Ranks plugin.

- Tracks shots, hits, misses, damage, kills, deaths and headshots per weapon, plus hits
  and damage per hitgroup — both overall and broken down per weapon.
- Missed shots are counted as a hitgroup bucket, so the breakdown accounts for every
  trigger pull rather than only the ones that landed.
- `!weaponstats` (alias `!ws`) and the hitgroup breakdown.
- Bot involvement is counted only when `CountAgainstBots` / `CountFromBots` are enabled,
  matching core's `PointsForBots` default of off.
- Figures include the caller's unflushed session delta, so a reading never lags a flush
  interval behind.
- Published `Ranks.WeaponStats.Api.v1` for other plugins to read the same totals.

# Changelog

Notable changes to Ranks_EloPoints, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_EloPoints.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.2.0] - 2026-11-17

Worth updating on every server that runs it.

- Ratings now build on every match a player stays for. Before, each match was rated from
  the rating the player joined with, so only their last match before leaving counted.
- A player's exact rating is no longer replaced by their rounded points when they join.
- Players moving over from core's own scoring keep their points as their starting rating.
  Some started from `Rank.StartPoints` instead.
- A player whose Steam authorization finishes after they enter the server, or who enters
  before Ranks is loaded, is now rated. Before, they went unrated until they reconnected.
- A stored rating that cannot be read when a player joins, for example during a database
  restart, is read again over the next minute instead of the player being rated from their
  rounded points for good.
- Reloading the module on a running server, from the console or from another plugin, now
  rates the players already there from the next match on, instead of nobody until they
  reconnected.
- Reloading Ranks on a running server no longer stops resets, new seasons and admin point
  commands from reaching ratings.
- With Ranks 1.5.0, core's own scoring switches off by itself while this module runs, so
  `Rank.UseCoreRankingLogic` no longer needs changing. On an older Ranks, keep it at `false`.
- `!setpoints`, `!givepoints` and `!removepoints` now change the player's rating, instead of
  being undone at their next reconnect or match end
  ([wiki](https://github.com/SwiftlyS2-Plugins/Ranks/wiki/Module-EloPoints#admin-commands)).
- A purge that fails, for example during an outage, is retried with the next save instead
  of leaving the purged players their old ratings.
- A reset or purge the database refuses three times, for example without `DELETE`
  permission, is dropped with an error. Before, such a reset stopped every later save.

## [1.1.2] - 2026-11-09

- No changes; released alongside Ranks 1.4.0.

## [1.1.1] - 2026-11-08

- No changes; released alongside Ranks 1.3.1.

## [1.1.0] - 2026-11-06

Requires Ranks 1.1.0. Worth updating if you use `!resetrank` or `!resetmyrank`.

- Ratings restart with each season when core's `Database.VirtualPurgeDays` is set: everyone
  starts the new season from `Rank.StartPoints`, and every past season's final ratings are kept.
- `!resetrank` and `!resetmyrank` now clear the rating for good. Before, the old rating came
  back at the end of the player's next match.
- A match still in progress when a season ends goes unrated.
- A player whose stored rating has not loaded by the end of a match is left unrated for it,
  instead of being dropped to `Rank.MinPoints`.
- First boot adds a `period` column to `ranks_elopoints` and creates the
  `ranks_elopoints_history` table. Existing ratings carry on unchanged while seasons are off.
- `Ranks.EloPoints.Api.v1` can read a past season's standings and leaderboard. Its existing
  calls now answer for the current season only.

## [1.0.1] - 2026-11-04

- Required by Ranks 1.0.4, which no longer loads 1.0.0 of this module.
- Ratings now move only on rounds Ranks counts, so `Rank.MinPlayers`, warmup and bot rules
  apply to both the same way. A drawn round no longer counts as a loss.

## [1.0.0] - 2026-10-26

First release. Faceit-style Elo rating in place of core's per-kill scoring.

- Rating is settled once per match from the result, not per kill. Set
  `Rank.UseCoreRankingLogic` to `false` so this module owns scoring.
- Shrinkage keeps a short session from counting as a full result, and a performance bonus
  rewards carrying a losing team.
- Abandoning a match applies a configurable penalty.
- Published `Ranks.EloPoints.Api.v1` for standings, the leaderboard and whether a rated
  match is in progress.

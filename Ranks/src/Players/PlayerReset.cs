using Ranks.Ranking;
using Ranks.Scoreboard;

namespace Ranks.Players;

/// <summary>Wipes one player's progression, from either the command or the Settings menu.</summary>
/// <remarks>
/// Shared so the two entry points cannot drift: the command's copy already omitted
/// <c>Headshots</c>, <c>Hits</c> and <c>Misses</c> when those columns were added, which left a
/// "reset" that silently kept a player's accuracy history.
///
/// <c>Name</c>, <c>FirstSeen</c> and <c>ShowPointMessages</c> deliberately survive \u2014 identity and
/// preferences are not progression, and clearing <c>FirstSeen</c> would make the row look new to
/// the inactivity purge.
/// </remarks>
internal sealed class PlayerReset
{
    private readonly RankLadder _ladder;
    private readonly PlayerSessions _sessions;
    private readonly ScoreboardService _scoreboard;

    public PlayerReset(RankLadder ladder, PlayerSessions sessions, ScoreboardService scoreboard)
    {
        _ladder = ladder;
        _sessions = sessions;
        _scoreboard = scoreboard;
    }

    public void Apply(PlayerRecord record)
    {
        record.Points = 0;
        record.Kills = 0;
        record.Deaths = 0;
        record.Assists = 0;
        record.Headshots = 0;
        record.Hits = 0;
        record.Misses = 0;
        record.RoundWins = 0;
        record.RoundLosses = 0;
        record.MatchWins = 0;
        record.MatchLosses = 0;
        record.BombsPlanted = 0;
        record.BombsDefused = 0;
        record.Killstreak = 0;
        record.PendingSummaryPoints = 0;
        record.RankIndex = _ladder.IndexFor(0);
        record.Dirty = true;

        // Saved immediately so a disconnect before the next autosave cannot resurrect the old row.
        _ = _sessions.SavePlayerAsync(record.SteamId);
        _scoreboard.Apply(record.SteamId);
    }
}

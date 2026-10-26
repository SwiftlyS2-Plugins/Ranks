using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Players;

namespace Ranks.Scoreboard;

/// <summary>Writes a player's rank onto the scoreboard, on change rather than on a tick.</summary>
/// <remarks>
/// Every networked write is paired with its notifier. Without it the value changes server-side and
/// is never networked — the scoreboard keeps rendering the old one, and the bug looks exactly like
/// a failed points calculation.
/// </remarks>
internal sealed class ScoreboardService
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly ILogger _logger;

    public ScoreboardService(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        RankLadder ladder,
        PlayerRegistry registry,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _ladder = ladder;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>The tag core would write, or null when tags are off or the player is unknown.</summary>
    /// <remarks>
    /// Read-only and side-effect free by contract: a tag arbiter has to be able to ask without
    /// fighting the thing it is arbitrating (FR-API-02).
    /// </remarks>
    public string? DesiredClanTag(ulong steamId)
    {
        if (!_config.Current.Scoreboard.Clantags) return null;

        return _registry.BySteamId(steamId) is { } record ? _ladder.TierFor(record.Points).Tag : null;
    }

    public void Apply(ulong steamId)
    {
        var player = _core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: false);

        if (player is not null) Apply(player);
    }

    public void Apply(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);
        if (record is null) return;

        var config = _config.Current;

        try
        {
            var controller = player.Controller;
            if (controller is null) return;

            var displayPoints = record.ScoreboardOverride ?? record.Points;

            if (config.Scoreboard.Clantags) WriteClanTag(player, controller, _ladder.TierFor(record.Points).Tag);

            if (config.Scoreboard.ScoreSync)
            {
                controller.Score = displayPoints;
                controller.ScoreUpdated();
            }

            WriteRankIcon(config, controller, record, displayPoints);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not update the scoreboard for {SteamId}.", player.SteamID);
        }
    }

    public void ApplyAll()
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                Apply(player);
    }

    private void WriteClanTag(IPlayer player, SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerController controller, string tag)
    {
        if (controller.Clan == tag) return;

        controller.Clan = tag;
        controller.ClanUpdated();

        // The scoreboard will not pick the tag up until its next natural redraw without this.
        _core.GameEvent.FireToPlayer<EventNextlevelChanged>(player.Slot);
    }

    /// <remarks>
    /// In Premier mode CompetitiveRanking carries the point total itself and CS2 colours it by its
    /// own band, so SkillGroup and the offset are ignored — and that is what makes a seven-band
    /// ladder legible, since the number moves every round while the colour does not.
    /// </remarks>
    private void WriteRankIcon(
        RanksConfig config,
        SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerController controller,
        PlayerRecord record,
        int displayPoints)
    {
        var mode = config.Scoreboard.RankMode;

        if (mode == RankDisplayMode.Off) return;

        var value = mode == RankDisplayMode.Competitive
            ? CompetitiveIcon(config, _ladder.TierFor(record.Points).SkillGroup)
            : displayPoints;

        controller.CompetitiveRankType = (byte)mode;
        controller.CompetitiveRankTypeUpdated();

        controller.CompetitiveRanking = value;
        controller.CompetitiveRankingUpdated();
    }

    /// <remarks>
    /// Reads the ladder entry's own SkillGroup rather than its position, so an 18-tier ladder need
    /// not map 1:1 onto CS2's 18 skill groups and several tiers may deliberately share an icon.
    /// Clamped <em>after</em> the offset, which exists for servers shipping a replacement icon set
    /// whose indices start at a different base.
    /// </remarks>
    private static int CompetitiveIcon(RanksConfig config, int skillGroup)
    {
        const int MaxSkillGroup = 18;

        return Math.Clamp(skillGroup + config.Scoreboard.SkillGroupOffset, 0, MaxSkillGroup);
    }
}

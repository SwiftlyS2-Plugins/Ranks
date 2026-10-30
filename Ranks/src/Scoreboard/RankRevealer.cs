using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace Ranks.Scoreboard;

/// <summary>Reveals every player's rank icon to everyone, not just their own (FR-SCB-04).</summary>
/// <remarks>
/// <c>CCSUsrMsg_ServerRankRevealAll</c> is a signal, not data: the client reveals every player's
/// rank icon on receipt and the two payload fields are ignored for this purpose. CS2 sends it once
/// per map, so anyone connecting later — or whose client drops it — sees blank icons for the rest
/// of the map. There is no matching "hide", which is why re-sending is always safe and the config
/// keys are all about <em>when</em>.
/// <para>
/// Handlers live outside the plugin class, so the instance <b>must</b> be passed to
/// <c>Core.Registrator.Register</c> in Load or <see cref="OnRoundStart"/> is silently inert.
/// </para>
/// </remarks>
internal sealed class RankRevealer : IDisposable
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly DetailLog _detail;
    private readonly ILogger _logger;

    private bool _listening;

    public RankRevealer(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        DetailLog detail,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _detail = detail;
        _logger = logger;
    }

    /// <summary>Starts the join trigger. Idempotent.</summary>
    public void Start()
    {
        if (_listening) return;

        _core.Event.OnClientPutInServer += OnPutInServer;
        _listening = true;
    }

    public void Dispose()
    {
        if (!_listening) return;

        _core.Event.OnClientPutInServer -= OnPutInServer;
        _listening = false;
    }

    public void RevealToAll() => Send(message => message.SendToAllPlayers(), "all players");

    /// <remarks>
    /// Round start rather than freeze end: the scoreboard is open during the freeze, so revealing
    /// after it would show the icons appearing late to anyone already looking.
    /// </remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        if (_config.Current.Scoreboard.Reveal.OnRoundStart)
        {
            RevealToAll();
            _detail.Write("Ranks: revealed ranks at round start.");
        }

        return HookResult.Continue;
    }

    /// <remarks>
    /// Broadcast rather than sent to the one player: their rank changed, so it is everyone else's
    /// view that is now stale.
    /// </remarks>
    public void OnRankChanged(ulong steamId)
    {
        if (!_config.Current.Scoreboard.Reveal.OnRankChange) return;

        RevealToAll();
        _detail.Write("Ranks: revealed ranks after {SteamId} changed rank.", steamId);
    }

    /// <remarks>
    /// Delayed: a client that has only just entered the server is not ready to act on the reveal,
    /// and the message carries no retry of its own.
    /// </remarks>
    private void OnPutInServer(IOnClientPutInServerEvent e)
    {
        var reveal = _config.Current.Scoreboard.Reveal;

        if (!reveal.OnPlayerJoin) return;

        var slot = e.PlayerId;

        _core.Scheduler.DelayBySeconds(reveal.JoinDelaySeconds, () =>
        {
            // They may have left during the delay; sending to a vacated slot is pointless rather
            // than harmful, but the check keeps the log honest.
            if (!_core.PlayerManager.IsPlayerOnline(slot)) return;

            Send(message => message.SendToPlayer(slot), $"slot {slot}");
            _detail.Write("Ranks: revealed ranks to joining slot {Slot}.", slot);
        });
    }

    /// <remarks>
    /// Guarded because a net message reaches native code: a malformed send would otherwise take
    /// the server down over a cosmetic feature. Warning rather than Error — nothing is lost, the
    /// next round's broadcast tries again.
    /// </remarks>
    private void Send(Action<CCSUsrMsg_ServerRankRevealAll> send, string audience)
    {
        try
        {
            using var message = _core.NetMessage.Create<CCSUsrMsg_ServerRankRevealAll>();

            send(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not send the rank reveal to {Audience}.", audience);
        }
    }
}

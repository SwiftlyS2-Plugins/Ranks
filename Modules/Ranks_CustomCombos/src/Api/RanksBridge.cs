using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.CustomCombos.Combos;
using Ranks.CustomCombos.Stats;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/>, a plain bool. <see cref="ComboArbiter"/> lives here for
/// the same reason — the rule is "exactly one file", not "exactly one class".
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly ComboCounters _counters;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining. The arbiter is held the same way —
    // it implements a contract interface, so even constructing it resolves Ranks.Contract.
    private object? _api;
    private object? _module;
    private object? _arbiter;

    private ComboMatcher _matcher;
    private bool _suppress;

    public RanksBridge(ISwiftlyCore core, Writer writer, ComboCounters counters, ComboMatcher matcher, bool suppress, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _counters = counters;
        _matcher = matcher;
        _suppress = suppress;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Raised once core reports ready, so the plugin can run its one-shot wiring.</summary>
    public event Action? CoreReady;

    /// <summary>Swaps the rule set in after a config reload.</summary>
    /// <remarks>Held even while core is absent, so the arbiter is built with current rules.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void UpdateRules(ComboMatcher matcher, bool suppress)
    {
        _matcher = matcher;
        _suppress = suppress;

        if (_arbiter is ComboArbiter arbiter) arbiter.UpdateRules(matcher, suppress);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ResetRound()
    {
        if (_arbiter is ComboArbiter arbiter) arbiter.ResetRound();
    }

    /// <summary>Refreshes core's bot policy onto the arbiter. No-op while core is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void RefreshBotPolicy()
    {
        if (_api is not IRanksApi api || _arbiter is not ComboArbiter arbiter) return;

        arbiter.UpdatePointsForBots(api.GetPointSettings().PointsForBots);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager)
    {
        try
        {
            if (!interfaceManager.HasSharedInterface(IRanksApi.InterfaceKey))
            {
                // Fail-closed rather than an early return: leaving a stale reference behind would
                // point at a disposed provider after the next handshake rebuild.
                _api = null;
                IsAvailable = false;

                return false;
            }

            var api = interfaceManager.GetSharedInterface<IRanksApi>(IRanksApi.InterfaceKey);

            _api = api;
            IsAvailable = api is not null;

            if (api is not null) Attach(api);

            return IsAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_CustomCombos: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        _module ??= new CustomCombosModule(api, _core, _writer, () => CoreReady?.Invoke());

        // Constructed here rather than in the plugin's Load: ComboArbiter implements a contract
        // interface, so merely naming it outside an isolated method would resolve Ranks.Contract
        // on a server that has no Ranks installed.
        _arbiter ??= new ComboArbiter(_core, _matcher, _suppress, _counters, _logger);

        ((CustomCombosModule)_module).Register();
        ((CustomCombosModule)_module).HandOverMigrations(typeof(RanksBridge).Assembly);

        // Registering the arbiter is what makes core consult us before it awards a kill.
        api.SetKillArbiter((ComboArbiter)_arbiter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        // Cleared before the module unregisters: core holding a reference into an unloaded
        // assembly is the one failure here that outlives this plugin.
        if (_api is IRanksApi api) api.SetKillArbiter(null);

        if (_module is CustomCombosModule module) module.Unregister();

        _module = null;
        _arbiter = null;
        _api = null;
        IsAvailable = false;
    }
}

/// <summary>This module's hook into core's lifecycle.</summary>
internal sealed class CustomCombosModule : RanksModuleBase
{
    private readonly Writer _writer;
    private readonly Action _onCoreReady;

    public CustomCombosModule(IRanksApi api, ISwiftlyCore core, Writer writer, Action onCoreReady)
        : base(api, core)
    {
        _writer = writer;
        _onCoreReady = onCoreReady;
    }

    public override string ModuleId => "Ranks_CustomCombos";

    /// <summary>Hands this assembly's migrations to core.</summary>
    public void HandOverMigrations(Assembly assembly) => RegisterMigrations(assembly);

    protected override void OnCoreReady()
    {
        _writer.Configure(ConnectionName, Table("ranks_combos"));
        _onCoreReady();
    }
}

/// <summary>Answers core's per-kill question (FR-API-11).</summary>
/// <remarks>
/// Converts core's context into a contract-free <see cref="KillFacts"/> immediately, so the
/// matcher and its self-check never name a contract type.
/// </remarks>
internal sealed class ComboArbiter : IRanksKillArbiter
{
    private readonly ISwiftlyCore _core;
    private readonly ComboCounters _counters;
    private readonly ILogger _logger;

    // Same-tick runs, keyed by attacker then combo. Cleared on the next tick rather than compared
    // against a tick number: every death of one grenade detonation is dispatched within the frame,
    // so "until NextTick runs" is exactly the window a Count combo means.
    private readonly Dictionary<ulong, Dictionary<string, int>> _sameTick = [];
    private bool _clearScheduled;

    private ComboMatcher _matcher;
    private bool _suppress;
    // Core's own default, so a policy that never arrives under-counts rather than over-counts.
    private bool _countBots;
    private bool _firstBloodTaken;

    public ComboArbiter(ISwiftlyCore core, ComboMatcher matcher, bool suppress, ComboCounters counters, ILogger logger)
    {
        _core = core;
        _matcher = matcher;
        _suppress = suppress;
        _counters = counters;
        _logger = logger;
    }

    public void UpdateRules(ComboMatcher matcher, bool suppress)
    {
        _matcher = matcher;
        _suppress = suppress;
    }

    /// <remarks>Refreshed on the flush timer rather than read per kill: <c>GetPointSettings</c>
    /// builds a record and a dictionary, and this runs inside core's award path.</remarks>
    public void UpdatePointsForBots(bool countBots) => _countBots = countBots;

    public void ResetRound() => _firstBloodTaken = false;

    public RanksKillClaim? ClaimKill(RanksKillContext context)
    {
        try
        {
            var firstBlood = !_firstBloodTaken;

            var facts = new KillFacts(
                context.AttackerSteamId,
                context.VictimSteamId,
                context.VictimIsBot,
                context.Weapon,
                context.Distance,
                context.HitGroup,
                context.Attributes,
                firstBlood);

            _firstBloodTaken = true;

            ScheduleTickClear();

            var rule = _matcher.Select(facts, r => Bump(context.AttackerSteamId, r.Key));
            if (rule is null) return null;

            // Counted here rather than after the award: a context gate refusing the points does not
            // unmake the combo, and counters are facts (FR-PTS-03). A bot victim with core's
            // PointsForBots off is the one case that suppresses the counter too, matching core.
            if (_countBots || !context.VictimIsBot)
                _counters.Record(context.AttackerSteamId, rule.Key);

            return new RanksKillClaim(
                $"combo.{rule.Key}",
                rule.Points,
                Describe(rule.MessageKey),
                _suppress);
        }
        catch (Exception ex)
        {
            // Core treats a throw as "no claim", but logging it here names the module.
            _logger.LogError(ex, "Ranks_CustomCombos: failed to evaluate a kill; core is awarding it.");
            return null;
        }
    }

    private int Bump(ulong attacker, string comboKey)
    {
        if (!_sameTick.TryGetValue(attacker, out var combos))
        {
            combos = new Dictionary<string, int>(StringComparer.Ordinal);
            _sameTick[attacker] = combos;
        }

        var next = combos.GetValueOrDefault(comboKey) + 1;
        combos[comboKey] = next;

        return next;
    }

    private void ScheduleTickClear()
    {
        if (_clearScheduled) return;

        _clearScheduled = true;

        _core.Scheduler.NextTick(() =>
        {
            _sameTick.Clear();
            _clearScheduled = false;
        });
    }

    /// <remarks>
    /// Combos are operator-defined, so a missing translation is the normal case rather than an edge
    /// case — and the localizer answers a miss with <c>{lang}.{key}</c>. Falling back to the bare
    /// key is ugly but readable, where the marker is neither.
    /// </remarks>
    private string Describe(string messageKey)
    {
        var resolved = _core.Localizer[messageKey];

        return string.IsNullOrEmpty(resolved)
               || string.Equals(resolved, messageKey, StringComparison.Ordinal)
               || resolved.EndsWith('.' + messageKey, StringComparison.Ordinal)
            ? messageKey
            : resolved;
    }
}

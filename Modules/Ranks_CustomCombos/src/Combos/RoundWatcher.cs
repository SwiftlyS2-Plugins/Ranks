using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;

namespace Ranks.CustomCombos.Combos;

/// <summary>Tells the arbiter when a round starts, so <c>first_blood</c> can arm again.</summary>
/// <remarks>
/// A separate class holding the <c>[GameEventHandler]</c> method, and deliberately naming no
/// contract type: <c>Core.Registrator.Register</c> reflects over whatever it is handed, and
/// reflecting over the arbiter would resolve <c>Ranks.Contract</c> on a server without Ranks.
/// The callback keeps that type out of here.
/// </remarks>
internal sealed class RoundWatcher
{
    private readonly Action _onRoundStart;

    public RoundWatcher(Action onRoundStart) => _onRoundStart = onRoundStart;

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        _onRoundStart();

        return HookResult.Continue;
    }
}

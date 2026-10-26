namespace Ranks.CustomCombos.Stats;

/// <summary>Per-player combo counts awaiting a flush. Game thread only.</summary>
/// <remarks>
/// Accumulated in memory because the arbiter runs inside core's award path, where a database round
/// trip would sit on the game thread for every kill.
/// </remarks>
internal sealed class ComboCounters
{
    private readonly Dictionary<ulong, Dictionary<string, int>> _pending = [];

    public void Record(ulong steamId, string comboKey)
    {
        if (!_pending.TryGetValue(steamId, out var combos))
        {
            combos = new Dictionary<string, int>(StringComparer.Ordinal);
            _pending[steamId] = combos;
        }

        combos[comboKey] = combos.GetValueOrDefault(comboKey) + 1;
    }

    /// <summary>Takes everything pending and clears it, in that order.</summary>
    public List<ComboDelta> Drain()
    {
        if (_pending.Count == 0) return [];

        var batch = new List<ComboDelta>(_pending.Count);

        foreach (var (steamId, combos) in _pending)
            foreach (var (key, count) in combos)
                batch.Add(new ComboDelta(steamId, key, count));

        _pending.Clear();

        return batch;
    }

    /// <summary>One player's unflushed counts, without clearing them.</summary>
    /// <remarks>
    /// Copied rather than handed out: the caller is the published API, whose continuation runs off
    /// the game thread this dictionary belongs to.
    /// </remarks>
    public Dictionary<string, int> Peek(ulong steamId) =>
        _pending.TryGetValue(steamId, out var combos)
            ? new Dictionary<string, int>(combos, StringComparer.Ordinal)
            : [];
}

internal readonly record struct ComboDelta(ulong SteamId, string ComboKey, int Count);

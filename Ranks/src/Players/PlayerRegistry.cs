using System.Collections.Concurrent;

namespace Ranks.Players;

/// <summary>Resident player records: a dictionary of truth and a slot array for the hot path.</summary>
/// <remarks>
/// Both are required (NFR-02). Caching the <em>record reference</em> in the slot array rather than
/// an identity is what separates 2.8–3.3 ns/op from 11.6; clearing the slot on disconnect is the
/// invariant that makes it safe. The dictionary additionally answers "adopt this reconnecting
/// SteamID", which a slot array cannot.
/// </remarks>
internal sealed class PlayerRegistry
{
    /// <remarks>CS2's hard player limit; IPlayer.Slot indexes into it directly.</remarks>
    private const int MaxSlots = 64;

    private readonly ConcurrentDictionary<ulong, PlayerRecord> _records = new();
    private readonly PlayerRecord?[] _slots = new PlayerRecord?[MaxSlots];

    public IReadOnlyCollection<PlayerRecord> All => (IReadOnlyCollection<PlayerRecord>)_records.Values;

    public int Count => _records.Count;

    /// <summary>The hot-path lookup. Null when the slot holds no tracked player.</summary>
    public PlayerRecord? BySlot(int slot) =>
        (uint)slot < MaxSlots ? _slots[slot] : null;

    public PlayerRecord? BySteamId(ulong steamId) =>
        _records.TryGetValue(steamId, out var record) ? record : null;

    public bool IsTracked(ulong steamId) => _records.ContainsKey(steamId);

    /// <summary>Publishes a loaded record and binds it to the player's slot.</summary>
    /// <remarks>
    /// A reconnecting player adopts the resident record rather than a fresh one — it may still hold
    /// unsaved points, and replacing it would discard them.
    /// </remarks>
    public PlayerRecord Adopt(int slot, PlayerRecord record)
    {
        var resident = _records.GetOrAdd(record.SteamId, record);

        if ((uint)slot < MaxSlots) _slots[slot] = resident;

        return resident;
    }

    /// <remarks>
    /// Clears the slot only. The record stays resident until it is fully saved, so a disconnect
    /// cannot lose a session's points and a fast reconnect re-adopts it.
    /// <para>
    /// The display overlay is dropped here precisely because the record survives: re-adopting it
    /// would otherwise hand the next session a prestige deduction nothing has recomputed, and an
    /// unloaded module would leave every player permanently overlaid (FR-API-07).
    /// </para>
    /// </remarks>
    public void ReleaseSlot(int slot)
    {
        if ((uint)slot >= MaxSlots) return;

        if (_slots[slot] is { } record) record.DisplayOverride = null;

        _slots[slot] = null;
    }

    /// <summary>Drops a fully-saved record that no longer occupies a slot.</summary>
    public void Evict(ulong steamId) => _records.TryRemove(steamId, out _);

    /// <summary>Removes purged players so core stops writing rows it just deleted.</summary>
    public void EvictAll(IEnumerable<ulong> steamIds)
    {
        foreach (var steamId in steamIds) _records.TryRemove(steamId, out _);
    }

    public void Clear()
    {
        _records.Clear();
        Array.Clear(_slots);
    }
}

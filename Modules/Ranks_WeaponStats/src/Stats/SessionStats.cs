namespace Ranks.WeaponStats.Stats;

/// <summary>Per-weapon counters for one session.</summary>
internal sealed class WeaponCounters
{
    public long Shots;
    public long Hits;
    public long Damage;
    public long Kills;
    public long Deaths;
    public long Headshots;

    public bool IsEmpty => (Shots | Hits | Damage | Kills | Deaths | Headshots) == 0;
}

/// <summary>One player's unflushed deltas. Game thread only.</summary>
/// <remarks>
/// No synchronisation anywhere: every mutator and the snapshot pass all run on the game thread,
/// which is what makes the design race-free without a lock or a concurrent collection.
/// </remarks>
internal sealed class SessionStats
{
    /// <remarks>
    /// Twelve, not eleven. HitGroup_t runs GENERIC = 0 through SPECIAL = 11 with a hole at
    /// UNUSED = 9, so indexing by the raw enum value needs 12 slots — an 11-element array
    /// overflows on SPECIAL. Slot 9 stays zero forever and is skipped at flush time.
    /// </remarks>
    public const int HitGroupSlots = 12;

    public required ulong SteamId { get; init; }

    public Dictionary<string, WeaponCounters> Weapons { get; } = new(StringComparer.Ordinal);

    public long[] HitGroupHits { get; } = new long[HitGroupSlots];

    public long[] HitGroupDamage { get; } = new long[HitGroupSlots];

    public WeaponCounters For(string weapon)
    {
        if (Weapons.TryGetValue(weapon, out var counters)) return counters;

        counters = new WeaponCounters();
        Weapons[weapon] = counters;

        return counters;
    }

    /// <summary>Takes the accumulated deltas and zeroes them in place.</summary>
    /// <remarks>
    /// Detaching on the game thread is what lets the writer run off it and never touch a live
    /// session again. Zeroing in place rather than replacing the object keeps the slot reference
    /// valid for handlers that are mid-dispatch.
    /// </remarks>
    public StatsDelta? Detach() => Capture(reset: true);

    /// <summary>Copies the accumulated deltas without clearing them.</summary>
    /// <remarks>
    /// For the read API: the database holds only what was last flushed, so a query that ignored
    /// this would under-report by the whole of the player's current session — up to
    /// FlushIntervalSeconds behind, for exactly the player most likely to be asked about.
    /// Reading rather than flushing keeps a query from costing a write.
    /// </remarks>
    public StatsDelta? Peek() => Capture(reset: false);

    private StatsDelta? Capture(bool reset)
    {
        var weapons = new List<(string Weapon, WeaponCounters Counters)>();

        foreach (var (weapon, counters) in Weapons)
        {
            if (counters.IsEmpty) continue;

            weapons.Add((weapon, new WeaponCounters
            {
                Shots = counters.Shots,
                Hits = counters.Hits,
                Damage = counters.Damage,
                Kills = counters.Kills,
                Deaths = counters.Deaths,
                Headshots = counters.Headshots,
            }));

            if (!reset) continue;

            counters.Shots = 0;
            counters.Hits = 0;
            counters.Damage = 0;
            counters.Kills = 0;
            counters.Deaths = 0;
            counters.Headshots = 0;
        }

        var hitGroups = new List<(short HitGroup, long Hits, long Damage)>();

        for (short i = 0; i < HitGroupSlots; i++)
        {
            if (HitGroupHits[i] == 0 && HitGroupDamage[i] == 0) continue;

            hitGroups.Add((i, HitGroupHits[i], HitGroupDamage[i]));

            if (!reset) continue;

            HitGroupHits[i] = 0;
            HitGroupDamage[i] = 0;
        }

        return weapons.Count == 0 && hitGroups.Count == 0 ? null : new StatsDelta(SteamId, weapons, hitGroups);
    }
}

/// <summary>A detached batch, safe to hand to the writer.</summary>
internal sealed record StatsDelta(
    ulong SteamId,
    IReadOnlyList<(string Weapon, WeaponCounters Counters)> Weapons,
    IReadOnlyList<(short HitGroup, long Hits, long Damage)> HitGroups);

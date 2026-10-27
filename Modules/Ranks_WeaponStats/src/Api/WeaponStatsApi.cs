using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Contract;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;

namespace Ranks.WeaponStats.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Unlike <see cref="RanksBridge"/> this names its contract without any JIT isolation, and that is
/// correct: a provider hard-requires the assembly it publishes from, exactly as core does with
/// Ranks.Contract. This module ships that assembly itself, so it is present whenever the module is.
/// </remarks>
internal sealed class WeaponStatsApi : IWeaponStatsApi
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly Collector _collector;
    private readonly ILogger _logger;

    public WeaponStatsApi(ISwiftlyCore core, Writer writer, Collector collector, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _collector = collector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId)
    {
        var live = await PeekAsync(steamId);
        var stored = await _writer.ReadWeaponsAsync(steamId);

        var merged = new Dictionary<string, WeaponStatRow>(StringComparer.Ordinal);

        foreach (var (weapon, shots, hits, misses, damage, kills, deaths, headshots) in stored)
            merged[weapon] = new WeaponStatRow(weapon, shots, hits, misses, damage, kills, deaths, headshots);

        foreach (var (weapon, counters) in live?.Weapons ?? [])
        {
            var row = merged.GetValueOrDefault(weapon) ?? new WeaponStatRow(weapon, 0, 0, 0, 0, 0, 0, 0);

            merged[weapon] = row with
            {
                Shots = row.Shots + counters.Shots,
                Hits = row.Hits + counters.Hits,
                Misses = row.Misses + counters.Misses,
                Damage = row.Damage + counters.Damage,
                Kills = row.Kills + counters.Kills,
                Deaths = row.Deaths + counters.Deaths,
                Headshots = row.Headshots + counters.Headshots,
            };
        }

        return [.. merged.Values];
    }

    public async Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId)
    {
        var live = await PeekAsync(steamId);
        var stored = await _writer.ReadHitGroupsAsync(steamId);

        var merged = new Dictionary<short, HitGroupStatRow>();

        foreach (var (hitGroup, hits, damage) in stored)
            merged[hitGroup] = new HitGroupStatRow(hitGroup, hits, damage);

        foreach (var (hitGroup, hits, damage) in live?.HitGroups ?? [])
        {
            var row = merged.GetValueOrDefault(hitGroup) ?? new HitGroupStatRow(hitGroup, 0, 0);

            merged[hitGroup] = row with { Hits = row.Hits + hits, Damage = row.Damage + damage };
        }

        return [.. merged.Values];
    }

    public async Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon)
    {
        // Normalised so a caller passing "ak47" finds the row stored as "weapon_ak47"; the two
        // event spellings are exactly what WeaponNames exists to reconcile.
        var normalised = WeaponNames.Normalize(weapon);

        return (await GetWeaponsAsync(steamId)).FirstOrDefault(row => row.Weapon == normalised);
    }

    /// <remarks>
    /// Hops to the game thread: sessions are game-thread state and a consumer calls this from
    /// wherever it likes. The returned delta is a copy, so the continuation is free to use it
    /// off-thread afterwards.
    /// </remarks>
    private Task<StatsDelta?> PeekAsync(ulong steamId)
    {
        var completion = new TaskCompletionSource<StatsDelta?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _core.Scheduler.NextTick(() =>
        {
            try
            {
                completion.SetResult(_collector.PeekFor(steamId));
            }
            catch (Exception ex)
            {
                // Falling back to stored-only is better than failing the query: the answer is then
                // merely behind, which is what it would have been without this merge at all.
                _logger.LogWarning(ex, "Ranks_WeaponStats: could not read live session stats for {SteamId}.", steamId);
                completion.SetResult(null);
            }
        });

        return completion.Task;
    }
}

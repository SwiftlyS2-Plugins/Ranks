using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Combos;
using Ranks.CustomCombos.Contract;
using Ranks.CustomCombos.Stats;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Unlike <see cref="RanksBridge"/> this names its contract without any JIT isolation, and that is
/// correct: a provider hard-requires the assembly it publishes from, exactly as core does with
/// Ranks.Contract. This module ships that assembly itself, so it is present whenever the module is.
///
/// Answers from the database plus the unflushed counts, so a figure read here matches what
/// <c>!combos</c> prints rather than lagging a flush interval behind.
/// </remarks>
internal sealed class CustomCombosApi : ICustomCombosApi
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly ComboCounters _counters;
    private readonly ILogger _logger;

    private ComboMatcher _matcher;

    public CustomCombosApi(ISwiftlyCore core, Writer writer, ComboCounters counters, ComboMatcher matcher, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _counters = counters;
        _matcher = matcher;
        _logger = logger;
    }

    public void UpdateRules(ComboMatcher matcher) => _matcher = matcher;

    public IReadOnlyList<string> ConfiguredCombos => [.. _matcher.Rules.Select(rule => rule.Key)];

    public async Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId)
    {
        var live = await PeekAsync(steamId);
        var stored = await _writer.ReadAsync(steamId);

        var merged = new Dictionary<string, ComboStatRow>(StringComparer.Ordinal);

        foreach (var (comboKey, count, lastAt) in stored)
            merged[comboKey] = new ComboStatRow(comboKey, count, lastAt);

        foreach (var (comboKey, count) in live)
        {
            var row = merged.GetValueOrDefault(comboKey);

            // An unflushed combo has no stored row and therefore no stored timestamp; UtcNow is
            // the honest answer, since the count it describes was landed this session.
            merged[comboKey] = row is null
                ? new ComboStatRow(comboKey, count, DateTime.UtcNow)
                : row with { Count = row.Count + count, LastAt = DateTime.UtcNow };
        }

        return [.. merged.Values.OrderByDescending(row => row.Count)];
    }

    public async Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey) =>
        (await GetCombosAsync(steamId)).FirstOrDefault(row => string.Equals(row.ComboKey, comboKey, StringComparison.OrdinalIgnoreCase));

    /// <remarks>
    /// Hops to the game thread: the counters are game-thread state and a consumer calls this from
    /// wherever it likes. The returned map is a copy, so the continuation is free to use it
    /// off-thread afterwards.
    /// </remarks>
    private Task<Dictionary<string, int>> PeekAsync(ulong steamId)
    {
        var completion = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _core.Scheduler.NextTick(() =>
        {
            try
            {
                completion.SetResult(_counters.Peek(steamId));
            }
            catch (Exception ex)
            {
                // Falling back to stored-only is better than failing the query: the answer is then
                // merely behind, which is what it would have been without this merge at all.
                _logger.LogWarning(ex, "Ranks_CustomCombos: could not read unflushed counts for {SteamId}.", steamId);
                completion.SetResult([]);
            }
        });

        return completion.Task;
    }
}

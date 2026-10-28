using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Config;

namespace Ranks.CustomCombos.Combos;

/// <summary>Parses the configured combos and picks which one pays for a kill.</summary>
/// <remarks>
/// Pure with respect to the game: it takes <see cref="KillFacts"/> and a counter callback rather
/// than reaching for live state.
/// </remarks>
internal sealed class ComboMatcher
{
    private readonly List<ComboRule> _rules;

    private ComboMatcher(List<ComboRule> rules) => _rules = rules;

    public IReadOnlyList<ComboRule> Rules => _rules;

    /// <summary>Builds a matcher, reporting every rejected combo by name.</summary>
    public static ComboMatcher Build(CombosConfig config, ILogger? logger)
    {
        var rules = new List<ComboRule>();
        var order = 0;

        foreach (var (key, entry) in config.Rules)
        {
            if (!TryBuild(key, entry, config.SuppressCoreAwards, order, out var rule, out var error))
            {
                logger?.LogWarning("Ranks_CustomCombos: combo '{Combo}' was skipped — {Reason}.", key, error);
                continue;
            }

            rules.Add(rule!);
            order++;
        }

        // Most specific first, ties by config order: an operator reordering the file changes which
        // of two equally specific combos wins, and nothing else does.
        rules.Sort(static (a, b) =>
            b.RequiresAll.Count != a.RequiresAll.Count
                ? b.RequiresAll.Count.CompareTo(a.RequiresAll.Count)
                : a.Order.CompareTo(b.Order));

        return new ComboMatcher(rules);
    }

    internal static bool TryBuild(string key, ComboEntry entry, bool suppressing, int order, out ComboRule? rule, out string error)
    {
        rule = null;

        if (string.IsNullOrWhiteSpace(key))
        {
            error = "the key is empty";
            return false;
        }

        if (entry.RequiresAll.Count == 0)
        {
            error = "RequiresAll is empty, so it would match every kill";
            return false;
        }

        if (entry.Points == 0)
        {
            error = "Points is 0, so it would award nothing";
            return false;
        }

        var count = Math.Max(1, entry.Count);

        // Core consults this module per kill, so on a Count:3 combo the first two kills are already
        // paid by the time the third arrives — suppressing only the last is a number no operator
        // would predict.
        if (count > 1 && suppressing)
        {
            error = "Count above 1 cannot be combined with SuppressCoreAwards";
            return false;
        }

        if (!TryParseAll(entry.RequiresAll, out var all, out error)) return false;
        if (!TryParseAll(entry.RequiresNone, out var none, out error)) return false;

        foreach (var test in all)
        {
            if (none.Contains(test))
            {
                error = $"'{test.Text}' is in both RequiresAll and RequiresNone";
                return false;
            }
        }

        var message = string.IsNullOrWhiteSpace(entry.Message) ? $"combo.{key}" : entry.Message!.Trim();

        rule = new ComboRule(key, all, none, count, entry.Points, message, order);
        return true;
    }

    private static bool TryParseAll(List<string> source, out List<AttributeTest> parsed, out string error)
    {
        parsed = [];

        foreach (var raw in source)
        {
            if (!TryParse(raw, out var test, out error)) return false;
            if (!parsed.Contains(test)) parsed.Add(test);
        }

        error = string.Empty;
        return true;
    }

    internal static bool TryParse(string raw, out AttributeTest test, out string error)
    {
        test = default;
        error = string.Empty;

        var text = raw?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            error = "an attribute is empty";
            return false;
        }

        var colon = text.IndexOf(':');

        if (colon < 0)
        {
            if (string.Equals(text, "first_blood", StringComparison.OrdinalIgnoreCase))
            {
                test = new AttributeTest(AttributeKind.FirstBlood, "first_blood", 0);
                return true;
            }

            if (!KnownFlags.Contains(text))
            {
                error = $"'{text}' is not a known attribute";
                return false;
            }

            test = new AttributeTest(AttributeKind.Flag, text.ToLowerInvariant(), 0);
            return true;
        }

        var name = text[..colon].Trim().ToLowerInvariant();
        var value = text[(colon + 1)..].Trim();

        if (value.Length == 0)
        {
            error = $"'{text}' has no value after the colon";
            return false;
        }

        switch (name)
        {
            case "weapon":
                test = new AttributeTest(AttributeKind.Weapon, value, 0);
                return true;

            case "weapon_class":
                if (!WeaponClasses.All.Contains(value.ToLowerInvariant()))
                {
                    error = $"'{value}' is not a weapon class";
                    return false;
                }

                test = new AttributeTest(AttributeKind.WeaponClass, value.ToLowerInvariant(), 0);
                return true;

            case "hitgroup":
                if (!int.TryParse(value, out var group))
                {
                    error = $"'{value}' is not a hitgroup number";
                    return false;
                }

                test = new AttributeTest(AttributeKind.HitGroup, text, group);
                return true;

            case "distance":
                if (!int.TryParse(value, out var units) || units < 0)
                {
                    error = $"'{value}' is not a distance in units";
                    return false;
                }

                test = new AttributeTest(AttributeKind.Distance, text, units);
                return true;

            default:
                error = $"'{name}' is not a known attribute prefix";
                return false;
        }
    }

    /// <summary>The winning combo for this kill, or null when none matched.</summary>
    /// <remarks>
    /// <paramref name="bumpSameTickCount"/> is called for <em>every</em> combo that matched, not
    /// only the winner, and its return is that combo's run of matching kills on the current tick.
    /// Returning early instead would stall a less specific Count combo every time a more specific
    /// one took the kill \u2014 the run is about what happened, not about who was paid.
    ///
    /// Keeping it a callback leaves the tick bookkeeping with the caller, which owns the scheduler.
    /// </remarks>
    public ComboRule? Select(in KillFacts facts, Func<ComboRule, int> bumpSameTickCount)
    {
        ComboRule? winner = null;

        foreach (var rule in _rules)
        {
            if (!Matches(rule, facts)) continue;

            var seen = bumpSameTickCount(rule);

            // Rules are most-specific-first, so the first satisfied one is the winner; the loop
            // still runs on to bump the rest.
            if (winner is null && seen >= rule.Count) winner = rule;
        }

        return winner;
    }

    internal static bool Matches(ComboRule rule, in KillFacts facts)
    {
        foreach (var test in rule.RequiresAll)
            if (!test.Matches(facts)) return false;

        foreach (var test in rule.RequiresNone)
            if (test.Matches(facts)) return false;

        return true;
    }

    private static readonly HashSet<string> KnownFlags =
        new(KillAttributeNames.All, StringComparer.OrdinalIgnoreCase);
}

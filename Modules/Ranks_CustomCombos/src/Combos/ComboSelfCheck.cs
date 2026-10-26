using Ranks.CustomCombos.Config;

namespace Ranks.CustomCombos.Combos;

/// <summary>Assert-based check of the matcher. Throws rather than logs.</summary>
/// <remarks>
/// Every rule here is silent when wrong: a combo that loses to the wrong rival, a Count that fires
/// on the first kill, or a RequiresNone that never excludes all look like "the operator configured
/// it oddly" on a live server. Runs at Load, costs microseconds.
/// </remarks>
internal static class ComboSelfCheck
{
    public static void Run()
    {
        ClosestMatchWins();
        TiesGoToConfigOrder();
        RequiresNoneExcludes();
        CountNeedsTheWholeRun();
        LosingCountComboStillAccumulates();
        ValuedAttributesCompare();
        RejectsBadConfiguration();
        WeaponClassHandlesBothSpellings();
    }

    /// <summary>The more specific of two matching combos pays, not the sum and not the first.</summary>
    private static void ClosestMatchWins()
    {
        var matcher = Build(
            ("broad", ["headshot", "noscope"], [], 1, 10),
            ("narrow", ["headshot", "noscope", "penetrated"], [], 1, 25));

        var winner = Select(matcher, Facts("headshot", "noscope", "penetrated"));

        Assert(winner?.Key == "narrow", "the three-attribute combo should outrank the two-attribute one");
        Assert(winner?.Points == 25, "the winner's own points should be awarded, not a sum");
    }

    private static void TiesGoToConfigOrder()
    {
        var matcher = Build(
            ("first", ["headshot", "noscope"], [], 1, 5),
            ("second", ["headshot", "thrusmoke"], [], 1, 500));

        var winner = Select(matcher, Facts("headshot", "noscope", "thrusmoke"));

        Assert(winner?.Key == "first", "equally specific combos should resolve by config order, not by value");
    }

    private static void RequiresNoneExcludes()
    {
        var matcher = Build(("clean", ["headshot"], ["attackerblind"], 1, 10));

        Assert(Select(matcher, Facts("headshot")) is not null, "an unexcluded kill should match");
        Assert(Select(matcher, Facts("headshot", "attackerblind")) is null, "RequiresNone should exclude");
    }

    /// <summary>A Count combo pays on the Nth same-tick kill and not before.</summary>
    private static void CountNeedsTheWholeRun()
    {
        var matcher = Build(("triple", ["headshot"], [], 3, 100));
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        int Bump(ComboRule rule) => seen[rule.Key] = seen.GetValueOrDefault(rule.Key) + 1;

        var facts = Facts("headshot");

        Assert(matcher.Select(facts, Bump) is null, "the first kill of a Count:3 combo must not pay");
        Assert(matcher.Select(facts, Bump) is null, "the second kill of a Count:3 combo must not pay");
        Assert(matcher.Select(facts, Bump)?.Key == "triple", "the third kill should complete the run");
    }

    /// <summary>A Count combo keeps its run even on kills a more specific combo took.</summary>
    /// <remarks>
    /// Silent when wrong: the specific combo still pays every kill, so the only symptom is that the
    /// broader one never fires — indistinguishable from it being hard to trigger.
    /// </remarks>
    private static void LosingCountComboStillAccumulates()
    {
        // The pair is ordered so the Count:2 combo is the LESS specific of the two, and therefore
        // sits after the winner in resolution order.
        var matcher = Build(
            ("specific", ["headshot", "noscope"], [], 1, 10),
            ("broad", ["headshot"], [], 2, 100));

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        int Bump(ComboRule rule) => seen[rule.Key] = seen.GetValueOrDefault(rule.Key) + 1;

        var facts = Facts("headshot", "noscope");

        Assert(matcher.Select(facts, Bump)?.Key == "specific", "the more specific combo should take the first kill");
        Assert(seen.GetValueOrDefault("broad") == 1, "the broader Count combo should still have counted that kill");

        Assert(matcher.Select(facts, Bump)?.Key == "specific", "the more specific combo should take the second kill too");
        Assert(seen.GetValueOrDefault("broad") == 2, "the broader combo's run should have reached its Count");
    }

    private static void ValuedAttributesCompare()
    {
        var matcher = Build(("far", ["distance:2000"], [], 1, 10));

        Assert(Select(matcher, Facts() with { Distance = 2500 }) is not null, "distance should match at or past the threshold");
        Assert(Select(matcher, Facts() with { Distance = 1999 }) is null, "distance below the threshold should not match");

        var byGroup = Build(("leg", ["hitgroup:6"], [], 1, 10));

        Assert(Select(byGroup, Facts() with { HitGroup = 6 }) is not null, "hitgroup should match on equality");
        Assert(Select(byGroup, Facts() with { HitGroup = 1 }) is null, "a different hitgroup should not match");
    }

    private static void RejectsBadConfiguration()
    {
        Assert(!TryBuild("x", ["nonsense"], [], 1, 10, false), "an unknown attribute should be rejected");
        Assert(!TryBuild("x", [], [], 1, 10, false), "an empty RequiresAll should be rejected");
        Assert(!TryBuild("x", ["headshot"], [], 1, 0, false), "a zero-point combo should be rejected");
        Assert(!TryBuild("x", ["headshot"], ["headshot"], 1, 10, false), "an attribute in both lists should be rejected");
        Assert(!TryBuild("x", ["headshot"], [], 3, 10, true), "Count above 1 with suppression should be rejected");
        Assert(TryBuild("x", ["headshot"], [], 3, 10, false), "Count above 1 without suppression is legal");
        Assert(!TryBuild("x", ["weapon_class:bazooka"], [], 1, 10, false), "an unknown weapon class should be rejected");
        Assert(!TryBuild("x", ["distance:far"], [], 1, 10, false), "a non-numeric distance should be rejected");
    }

    /// <summary>The event carries both spellings, and both must reach the same class.</summary>
    private static void WeaponClassHandlesBothSpellings()
    {
        Assert(WeaponClasses.Of("weapon_ak47") == WeaponClasses.Rifle, "the prefixed name should resolve");
        Assert(WeaponClasses.Of("ak47") == WeaponClasses.Rifle, "the bare name should resolve");
        Assert(WeaponClasses.Of("weapon_knife_karambit") == WeaponClasses.Knife, "a knife skin should resolve to knife");
        Assert(WeaponClasses.Of("bayonet") == WeaponClasses.Knife, "the bayonet should resolve to knife");
        Assert(WeaponClasses.Of("planted_c4") is null, "a non-weapon should belong to no class");
    }

    private static ComboMatcher Build(params (string Key, string[] All, string[] None, int Count, int Points)[] rules)
    {
        var config = new CombosConfig();

        foreach (var (key, all, none, count, points) in rules)
            config.Rules[key] = new ComboEntry { RequiresAll = [.. all], RequiresNone = [.. none], Count = count, Points = points };

        return ComboMatcher.Build(config, logger: null);
    }

    private static bool TryBuild(string key, string[] all, string[] none, int count, int points, bool suppressing)
    {
        var entry = new ComboEntry { RequiresAll = [.. all], RequiresNone = [.. none], Count = count, Points = points };

        return ComboMatcher.TryBuild(key, entry, suppressing, 0, out _, out _);
    }

    private static KillFacts Facts(params string[] attributes) =>
        new(1, 2, false, "weapon_ak47", 0, 0, new HashSet<string>(attributes, StringComparer.Ordinal), false);

    /// <summary>Selects with a fresh counter, so every combo completes a Count:1 run immediately.</summary>
    private static ComboRule? Select(ComboMatcher matcher, KillFacts facts)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        return matcher.Select(facts, rule => seen[rule.Key] = seen.GetValueOrDefault(rule.Key) + 1);
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException($"Ranks_CustomCombos: combo self-check failed — {because}.");
    }
}

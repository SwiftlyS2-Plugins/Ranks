using Ranks.Config;

namespace Ranks.Database;

/// <summary>Asserts the identifier rules that have no other guard.</summary>
/// <remarks>
/// Runs once at load because every failure mode here is silent: a wrong affix writes to a real
/// table that is simply the wrong one, and a ledger collision reports migrations as applied that
/// never ran. Microseconds, and the only check this logic gets — the repo has no test project.
/// </remarks>
internal static class TableNamesSelfCheck
{
    public static void Run()
    {
        SeparatorIsSuppliedByThePlugin();
        EmptyAffixesChangeNothing();
        ModuleLedgerDropsTheRanksPrefix();
        OverlongIdentifiersFoldDeterministically();
    }

    /// <remarks>"awp", "awp_" and "_awp" must agree, or an operator "fixing" one silently moves every table.</remarks>
    private static void SeparatorIsSuppliedByThePlugin()
    {
        var bare = Build("awp", "dm").Resolve("ranks_players");
        var trailing = Build("awp_", "dm_").Resolve("ranks_players");
        var leading = Build("_awp", "_dm").Resolve("ranks_players");

        Require(bare == "awp_ranks_players_dm", $"prefix/suffix normalisation produced '{bare}'");
        Require(trailing == bare, $"'awp_' produced '{trailing}', expected '{bare}'");
        Require(leading == bare, $"'_awp' produced '{leading}', expected '{bare}'");
    }

    private static void EmptyAffixesChangeNothing()
    {
        var resolved = Build("", "").Resolve("ranks_players");
        Require(resolved == "ranks_players", $"unaffixed name became '{resolved}'");

        var ledger = Build("", "").CoreLedger;
        Require(ledger == "ranks_migrations", $"core ledger became '{ledger}'");
    }

    private static void ModuleLedgerDropsTheRanksPrefix()
    {
        var ledger = Build("", "").LedgerFor("Ranks_WeaponStats");
        Require(ledger == "ranks_migrations_weaponstats", $"module ledger became '{ledger}'");

        var affixed = Build("awp", "").LedgerFor("Ranks_WeaponStats");
        Require(affixed == "awp_ranks_migrations_weaponstats", $"affixed module ledger became '{affixed}'");
    }

    private static void OverlongIdentifiersFoldDeterministically()
    {
        var overlong = new string('x', 80);
        var first = TableNames.Fit(overlong);
        var second = TableNames.Fit(overlong);

        Require(first.Length == 63, $"folded identifier was {first.Length} chars, expected 63");
        Require(first == second, "folding is not deterministic");
        Require(TableNames.Fit(new string('y', 80)) != first, "two different overlong names folded together");
    }

    private static TableNames Build(string prefix, string suffix) =>
        new(new DatabaseOptions { TablePrefix = prefix, TableSuffix = suffix });

    private static void Require(bool condition, string failure)
    {
        if (condition) return;

        throw new InvalidOperationException($"Ranks: table-name self-check failed — {failure}.");
    }
}

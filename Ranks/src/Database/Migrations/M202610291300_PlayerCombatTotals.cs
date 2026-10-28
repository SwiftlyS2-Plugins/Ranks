using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Adds the overall combat counters to the player row.</summary>
/// <remarks>
/// A second migration rather than an edit to <c>M202601010000</c>: that one has already run
/// everywhere, and an edited migration is recorded as applied without ever adding the columns.
///
/// These are core's own figures, gated by core's warmup and bot settings. Ranks_WeaponStats keeps
/// the per-weapon breakdown and gates it on its own switches, so the two reconcile only when those
/// settings agree — the same caveat that already applies to <c>kills</c>.
/// </remarks>
[Migration(202610291300)]
public sealed class M202610291300_PlayerCombatTotals : Migration
{
    private const string Table = "ranks_players";

    public override void Up()
    {
        // Defaults backfill every row that predates the columns: an existing player keeps their
        // history and simply starts counting these from zero.
        foreach (var column in (ReadOnlySpan<string>)["hits", "misses", "headshots"])
            if (!Schema.Table(Table).Column(column).Exists())
                Alter.Table(Table)
                    .AddColumn(column).AsInt32().NotNullable().WithDefaultValue(0);
    }

    /// <remarks>Empty: the columns hold player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

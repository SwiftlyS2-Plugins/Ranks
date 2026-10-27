using FluentMigrator;

namespace Ranks.WeaponStats.Migrations;

/// <summary>Adds the per-weapon missed-shot counter.</summary>
/// <remarks>
/// A second migration rather than an edit to <c>M202610121200</c>: that one may already have run,
/// and an edited migration is recorded as applied without ever adding the column. Core runs this,
/// not the module.
/// </remarks>
[Migration(202610271200)]
public sealed class M202610271200_WeaponMisses : Migration
{
    private const string WeaponStats = "ranks_weapon_stats";
    private const string Misses = "misses";

    public override void Up()
    {
        // The default backfills every row that predates the column; rows written before this
        // migration keep their shots and hits and simply start counting misses from zero.
        if (!Schema.Table(WeaponStats).Column(Misses).Exists())
            Alter.Table(WeaponStats)
                .AddColumn(Misses).AsInt64().NotNullable().WithDefaultValue(0);
    }

    /// <remarks>Empty: the column holds player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

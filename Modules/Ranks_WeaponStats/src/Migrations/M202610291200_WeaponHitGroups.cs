using FluentMigrator;

namespace Ranks.WeaponStats.Migrations;

/// <summary>Splits the hitgroup breakdown by weapon.</summary>
/// <remarks>
/// A third table rather than columns on <c>ranks_weapon_stats</c>: the hitgroup axis is 12 wide
/// with a permanent hole at 9, so folding it in would mean a dozen sparse columns per weapon.
/// Summing this table over weapons reproduces <c>ranks_hit_stats</c> exactly.
///
/// Only landed hits appear — a miss has no hitgroup. Core runs this migration, not the module.
/// </remarks>
[Migration(202610291200)]
public sealed class M202610291200_WeaponHitGroups : Migration
{
    private const string WeaponHitStats = "ranks_weapon_hit_stats";

    public override void Up()
    {
        // Inline composite PK for the same reasons as M202610121200: MySQL names every primary key
        // PRIMARY so an Exists() guard never fires, and the additive upsert needs the key to
        // conflict against.
        if (!Schema.Table(WeaponHitStats).Exists())
            Create.Table(WeaponHitStats)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("weapon").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("hitgroup").AsInt16().NotNullable().PrimaryKey()
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0);
    }

    /// <remarks>Empty: the table holds player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

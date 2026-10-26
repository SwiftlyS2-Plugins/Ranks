using FluentMigrator;

namespace Ranks.WeaponStats.Migrations;

/// <summary>This module's two tables. Core runs this migration, not the module.</summary>
/// <remarks>
/// The base names carry the <c>ranks_</c> namespace: core's affix resolver wraps a base that is
/// already <c>ranks_players</c> for core, so writing <c>ranks_weapon_stats</c> yields
/// <c>awp_ranks_weapon_stats</c> on a prefixed server. Dropping the namespace would put these
/// outside the group an operator filters on.
/// </remarks>
[Migration(202610121200)]
public sealed class M202610121200_Tables : Migration
{
    private const string WeaponStats = "ranks_weapon_stats";
    private const string HitStats = "ranks_hit_stats";

    public override void Up()
    {
        // The composite PK is declared inline because MySQL names every primary key PRIMARY, so a
        // Constraint("pk_x").Exists() guard is always false and the follow-up ADD CONSTRAINT then
        // fails with "Multiple primary key defined". Inline is also the only portable composite
        // key on SQLite. The PK itself is non-negotiable: without it the additive upsert silently
        // duplicates on MySQL and is rejected outright by PostgreSQL and SQLite.
        if (!Schema.Table(WeaponStats).Exists())
            Create.Table(WeaponStats)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("weapon").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("shots").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("kills").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("deaths").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("headshots").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(HitStats).Exists())
            Create.Table(HitStats)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("hitgroup").AsInt16().NotNullable().PrimaryKey()
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0);
    }

    /// <remarks>Empty: both tables hold player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

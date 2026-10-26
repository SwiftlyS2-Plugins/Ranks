using FluentMigrator;

namespace Ranks.CustomCombos.Migrations;

/// <summary>This module's counter table. Core runs this migration, not the module.</summary>
/// <remarks>
/// The base name carries the <c>ranks_</c> namespace: core's affix resolver wraps a base that is
/// already <c>ranks_players</c> for core, so writing <c>ranks_combos</c> yields
/// <c>awp_ranks_combos</c> on a prefixed server.
///
/// Tall rather than a column per combo: combos are operator-defined, so a wide table cannot have a
/// column for a key that does not exist yet.
/// </remarks>
[Migration(202610261200)]
public sealed class M202610261200_Combos : Migration
{
    private const string Combos = "ranks_combos";

    public override void Up()
    {
        // The composite PK is declared inline because MySQL names every primary key PRIMARY, so a
        // Constraint("pk_x").Exists() guard is always false and the follow-up ADD CONSTRAINT then
        // fails with "Multiple primary key defined". Inline is also the only portable composite
        // key on SQLite. The PK itself is non-negotiable: without it the additive upsert silently
        // duplicates on MySQL and is rejected outright by PostgreSQL and SQLite.
        if (!Schema.Table(Combos).Exists())
            Create.Table(Combos)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("combo_key").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("count").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("last_at").AsDateTime().NotNullable();
    }

    /// <remarks>Empty: the table holds player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

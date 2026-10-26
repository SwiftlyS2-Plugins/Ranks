using FluentMigrator;

namespace Ranks.EloPoints.Migrations;

/// <summary>This module's rating table. Core runs this migration, not the module.</summary>
/// <remarks>
/// The base name carries the <c>ranks_</c> namespace: core's affix resolver wraps a base that is
/// already <c>ranks_players</c> for core, so writing <c>ranks_elopoints</c> yields
/// <c>awp_ranks_elopoints</c> on a prefixed server.
/// </remarks>
[Migration(202610261200)]
public sealed class M202610261200_Rating : Migration
{
    private const string Rating = "ranks_elopoints";

    public override void Up()
    {
        // DECIMAL, not FLOAT: binary floating point makes the stored value depend on accumulation
        // order, so two servers writing the same deltas can disagree in the last digits — and the
        // value is then rounded for core's INT points column.
        if (!Schema.Table(Rating).Exists())
            Create.Table(Rating)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();
    }

    /// <remarks>Empty: the table holds player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}

using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Core's player record — FR-DB-04's first fixed-column table.</summary>
/// <remarks>
/// Every object is guarded individually (FR-DB-05 req. 2). One guard covering the table and its
/// index would skip the index while still recording the migration as applied, and MigrateUp()
/// never retries — MySQL DDL is not transactional, so a first run failing after Create.Table
/// reproduces that with no legacy database involved.
/// </remarks>
[Migration(202601010000)]
public sealed class M202601010000_Players : Migration
{
    private const string Table = "ranks_players";
    private const string LastSeenIndex = "ix_ranks_players_last_seen";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            Create.Table(Table)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("name").AsString(128).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn("points").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("kills").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("deaths").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("assists").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("round_wins").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("round_losses").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("match_wins").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("match_losses").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("bombs_planted").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("bombs_defused").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("first_seen").AsDateTime().NotNullable()
                .WithColumn("last_seen").AsDateTime().NotNullable();

        if (!Schema.Table(Table).Index(LastSeenIndex).Exists())
            Create.Index(LastSeenIndex).OnTable(Table).OnColumn("last_seen").Ascending();
    }

    /// <remarks>Drops the index only — the table holds player data (FR-DB-05 req. 6).</remarks>
    public override void Down()
    {
        if (Schema.Table(Table).Index(LastSeenIndex).Exists())
            Delete.Index(LastSeenIndex).OnTable(Table);
    }
}

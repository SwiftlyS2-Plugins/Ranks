using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Per-player preferences — FR-DB-04's second fixed-column table.</summary>
/// <remarks>
/// A new core preference is a new column and a new migration, deliberately: a key/value table here
/// would exist only to serve modules, which own their own storage (FR-API-06).
/// </remarks>
[Migration(202601010001)]
public sealed class M202601010001_Settings : Migration
{
    private const string Table = "ranks_settings";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            Create.Table(Table)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("show_point_messages").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    /// <remarks>Empty — the table holds player preferences (FR-DB-05 req. 6).</remarks>
    public override void Down()
    {
    }
}

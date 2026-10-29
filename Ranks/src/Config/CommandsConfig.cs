using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Ranks.Config;

/// <summary>One command's operator-editable surface.</summary>
/// <remarks>
/// Name, aliases and permission only. Help text is deliberately absent — it is player-facing, so it
/// resolves through <c>Core.Localizer</c> like every other player string, which also makes it
/// translatable (FR-CMD-01, FR-CMD-05).
/// </remarks>
internal sealed class CommandDefinition
{
    /// <summary>An empty string unregisters the command entirely.</summary>
    public string Command { get; set; } = "";

    public List<string> Aliases { get; set; } = [];

    /// <summary>Empty means everyone.</summary>
    public string Permission { get; set; } = "";
}

/// <summary>Command names, aliases and permissions (<c>commands.jsonc</c>).</summary>
internal sealed class CommandsConfig
{
    public const string Rank = "rank";
    public const string Ranks = "ranks";
    public const string Top = "top";
    public const string Stats = "stats";
    public const string ResetMyRank = "resetmyrank";
    public const string SetPoints = "setpoints";
    public const string GivePoints = "givepoints";
    public const string RemovePoints = "removepoints";
    public const string ResetRank = "resetrank";

    private const string AdminPermission = "ranks.admin";

    [Description("Command names, aliases and permissions. Set Command to an empty string to remove a command. Permission empty means everyone.")]
    public Dictionary<string, CommandDefinition> All { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Rank] = new() { Command = "rank", Aliases = ["myrank"] },
        [Ranks] = new() { Command = "ranks", Aliases = ["ranklist"] },
        [Top] = new() { Command = "top", Aliases = ["ranktop"] },
        [Stats] = new() { Command = "stats", Aliases = ["mystats"] },
        [ResetMyRank] = new() { Command = "resetmyrank" },
        [SetPoints] = new() { Command = "setpoints", Permission = AdminPermission },
        [GivePoints] = new() { Command = "givepoints", Permission = AdminPermission },
        [RemovePoints] = new() { Command = "removepoints", Permission = AdminPermission },
        [ResetRank] = new() { Command = "resetrank", Permission = AdminPermission },
    };

    /// <remarks>
    /// An alias list is a set, and the binder appends the file's entries onto the model's defaults
    /// — so the shipped file binds every default alias twice, and registering one twice is at best
    /// a wasted call.
    /// </remarks>
    public static void Validate(CommandsConfig config, ILogger logger)
    {
        foreach (var definition in config.All.Values)
            if (definition.Aliases.Count > 1)
                definition.Aliases = definition.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

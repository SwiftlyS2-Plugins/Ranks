using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Ranks.Config;

/// <summary>One ladder tier.</summary>
internal sealed class RankTier
{
    public string Name { get; set; } = "";

    /// <summary>Short form written into the clan tag.</summary>
    public string Tag { get; set; } = "";

    /// <summary>Chat colour token.</summary>
    public string Color { get; set; } = "default";

    /// <summary>Colour used when rendering menus.</summary>
    public string Hex { get; set; } = "#FFFFFF";

    /// <summary>Inclusive point threshold.</summary>
    public int Points { get; set; }

    /// <summary>Competitive skill-group index, used only when RankMode is Competitive.</summary>
    public int SkillGroup { get; set; } = 1;
}

/// <summary>The rank ladder (<c>ranks.jsonc</c>).</summary>
/// <remarks>
/// <see cref="Tiers"/> starts <em>empty</em>, unlike every other default in this plugin, and that is
/// load-bearing: the configuration binder appends JSON array entries onto a non-empty collection
/// initializer rather than replacing it, so a populated default plus the file written from it would
/// bind to fourteen tiers — and an operator swapping in the 18-entry competitive ladder would get
/// their 18 interleaved with core's 7. The shipped file is seeded by <see cref="LadderSeed"/>
/// instead.
/// </remarks>
internal sealed class LadderConfig
{
    [Description("Ordered rank ladder. A player's rank is the highest entry whose Points threshold they meet; the first entry must be 0.")]
    public List<RankTier> Tiers { get; set; } = [];

    /// <summary>Validates the ladder, throwing on a shape no player could be ranked against.</summary>
    public static void Validate(LadderConfig config, ILogger logger)
    {
        if (config.Tiers.Count == 0)
        {
            logger.LogWarning("Ranks: ranks.jsonc contains no tiers; falling back to the shipped Premier ladder.");
            config.Tiers = LadderSeed.Premier();
            return;
        }

        config.Tiers.Sort((a, b) => a.Points.CompareTo(b.Points));

        if (config.Tiers[0].Points != 0)
            throw new InvalidOperationException(
                "Ranks: the first ladder entry in ranks.jsonc must have Points: 0, " +
                $"but the lowest is {config.Tiers[0].Points}. A player below the lowest threshold would have no rank at all.");

        foreach (var tier in config.Tiers)
            if (string.IsNullOrWhiteSpace(tier.Name))
                throw new InvalidOperationException(
                    $"Ranks: the ladder entry at {tier.Points} points has no Name.");
    }
}

/// <summary>The shipped ladder, written to disk on first run.</summary>
/// <remarks>
/// Seeded as a file rather than as a model default so the binder cannot append it to itself. The
/// thresholds and colours are CS2's own Premier bands verbatim, so a player who knows Premier
/// already knows this ladder.
/// </remarks>
internal static class LadderSeed
{
    public static List<RankTier> Premier() =>
    [
        new() { Name = "Grey", Tag = "Grey", Color = "grey", Hex = "#B0C3D9", Points = 0, SkillGroup = 1 },
        new() { Name = "Light Blue", Tag = "LtBlue", Color = "lightblue", Hex = "#5E98D9", Points = 5000, SkillGroup = 4 },
        new() { Name = "Blue", Tag = "Blue", Color = "blue", Hex = "#4B69FF", Points = 10000, SkillGroup = 7 },
        new() { Name = "Purple", Tag = "Purple", Color = "purple", Hex = "#8847FF", Points = 15000, SkillGroup = 10 },
        new() { Name = "Pink", Tag = "Pink", Color = "magenta", Hex = "#D32CE6", Points = 20000, SkillGroup = 13 },
        new() { Name = "Red", Tag = "Red", Color = "red", Hex = "#EB4B4B", Points = 25000, SkillGroup = 16 },
        new() { Name = "Gold", Tag = "Gold", Color = "gold", Hex = "#CAAB05", Points = 30000, SkillGroup = 18 },
    ];

    /// <summary>The file written on first run, comments included.</summary>
    /// <remarks>
    /// Hand-written rather than serialized so the two consequences an operator needs before editing
    /// this file are stated where they will edit it — saturation, and what Competitive mode costs.
    /// </remarks>
    public static string FileContents()
    {
        var rows = string.Join(",\n", Premier().Select(t =>
            $$"""
                  { "Name": "{{t.Name}}", "Tag": "{{t.Tag}}", "Color": "{{t.Color}}", "Hex": "{{t.Hex}}", "Points": {{t.Points}}, "SkillGroup": {{t.SkillGroup}} }
            """.TrimEnd()));

        return $$"""
            // Ranks - rank ladder.
            //
            // A player's rank is the highest entry whose Points threshold they meet.
            // The first entry must be 0, or a player below the lowest threshold would have no rank.
            //
            // These are CS2's own Premier bands verbatim, so a player who knows Premier knows this ladder.
            // At the shipped point values an average regular earns roughly 190 points/hour, so Gold is
            // about 158 hours of play.
            //
            // Points never decay, so any fixed ladder saturates as a server ages: given long enough every
            // regular sits above the top threshold and the upper bands stop distinguishing anyone.
            // Re-tuning demotes players who have not lost a single point, so it is a deliberate decision
            // rather than routine maintenance.
            //
            // SkillGroup is read by the per-tier icon modes - Competitive and Wingman.
            // Premier (the default) renders the point total itself and colours it by CS2's own band,
            // which is what makes seven coarse bands legible - the colour is the tier, the number is
            // the progress bar. Switching to Competitive without also switching to a finer-grained
            // ladder removes the number and leaves seven icons; resources/examples ships an 18-entry
            // ladder for that case. Custom mode ignores SkillGroup and maps ladder POSITION onto a
            // replacement icon set through Scoreboard.CustomRankBase/Max/Margin instead.
            {
              "Ladder": {
                "Tiers": [
            {{rows}}
                ]
              }
            }

            """;
    }
}

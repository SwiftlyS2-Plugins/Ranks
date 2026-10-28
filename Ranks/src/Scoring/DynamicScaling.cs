namespace Ranks.Scoring;

/// <summary>Scales a kill or death by how far apart the two players are.</summary>
/// <remarks>
/// Dying to a much weaker player should hurt more and dying to a much stronger one less; without
/// it a top player and a brand-new one pay the same price for a death, which makes the top of the
/// ladder cheap to hold.
/// </remarks>
internal static class DynamicScaling
{
    /// <summary>The relative difference between victim and attacker, in <c>[-1, +1]</c>.</summary>
    /// <remarks>
    /// Always (victim − attacker), on both sides: it is a property of the matchup, not of whose
    /// points are changing, which is what makes the kill and death halves one mechanism.
    /// max(Pv, Pa) is the denominator because dividing by the attacker's points alone is violently
    /// sensitive whenever the attacker is near zero — precisely the fresh-player case this exists
    /// to handle.
    /// </remarks>
    public static double Difference(int victimPoints, int attackerPoints)
    {
        var denominator = Math.Max(victimPoints, attackerPoints);

        return denominator <= 0 ? 0.0 : (victimPoints - attackerPoints) / (double)denominator;
    }

    /// <summary>Turns a difference into a multiplier pinned at 1.0 when the players are equal.</summary>
    /// <remarks>
    /// Both halves are pinned at 1.0, so a near-equal matchup is a near-no-op and the configured
    /// value applies as written. Any replacement formula must preserve that anchor or the
    /// configured kill and death values stop meaning what they say.
    /// </remarks>
    public static double Multiplier(double difference, double min, double max) =>
        difference >= 0
            ? 1 + difference * (max - 1)
            : 1 + difference * (1 - min);
}

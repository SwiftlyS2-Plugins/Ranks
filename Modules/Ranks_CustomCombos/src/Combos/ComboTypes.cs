namespace Ranks.CustomCombos.Combos;

/// <summary>Core's kill facts, restated without naming a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// The projection exists for the JIT isolation rule: only the bridge file may name contract types,
/// so everything downstream works on this instead and stays loadable on a server with no Ranks
/// installed.
/// </remarks>
internal readonly record struct KillFacts(
    ulong AttackerSteamId,
    ulong? VictimSteamId,
    bool VictimIsBot,
    string Weapon,
    float Distance,
    int HitGroup,
    IReadOnlySet<string> Attributes,
    bool FirstBlood);

internal enum AttributeKind
{
    /// <summary>A flag core put in the attribute set.</summary>
    Flag,
    Weapon,
    WeaponClass,
    HitGroup,
    Distance,
    FirstBlood,
}

/// <summary>One configured attribute, parsed once at load.</summary>
/// <remarks>
/// Valued forms (<c>distance:2000</c>, <c>hitgroup:5</c>) cannot be flags in core's set: core would
/// have to pre-expand an entry for every threshold an operator might write, so it ships the number
/// and the comparison happens here.
/// </remarks>
internal readonly record struct AttributeTest(AttributeKind Kind, string Text, int Number)
{
    public bool Matches(in KillFacts facts) => Kind switch
    {
        AttributeKind.Flag => facts.Attributes.Contains(Text),
        AttributeKind.Weapon => string.Equals(facts.Weapon, Text, StringComparison.OrdinalIgnoreCase),
        AttributeKind.WeaponClass => string.Equals(WeaponClasses.Of(facts.Weapon), Text, StringComparison.Ordinal),
        AttributeKind.HitGroup => facts.HitGroup == Number,
        AttributeKind.Distance => facts.Distance >= Number,
        AttributeKind.FirstBlood => facts.FirstBlood,
        _ => false,
    };
}

/// <summary>A combo after parsing and validation.</summary>
internal sealed record ComboRule(
    string Key,
    IReadOnlyList<AttributeTest> RequiresAll,
    IReadOnlyList<AttributeTest> RequiresNone,
    int Count,
    int Points,
    string MessageKey,
    int Order);

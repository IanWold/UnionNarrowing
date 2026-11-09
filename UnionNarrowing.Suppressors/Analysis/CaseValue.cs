using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// A value of a union's case type that's a <c>bool</c> or an enum, as a case of the union:
/// <c>true</c>, an enum member's value (see <see cref="CaseSpace.ConstantKey"/>), or <see cref="CaseSpace.OtherValues"/> for the enum's values no member has.
/// Two enums' values don't mix.
/// </summary>
internal class CaseValue(ITypeSymbol type, object key) : System.IEquatable<CaseValue>
{
    public object Key { get; } = key;

    /// <summary>
    /// The bool or enum type.
    /// </summary>
    public ITypeSymbol Type { get; } = type;

    public bool Equals(CaseValue? other) =>
        other is not null && SymbolEqualityComparer.Default.Equals(Type, other.Type) && Equals(Key, other.Key);

    public override bool Equals(object? obj) =>
        Equals(obj as CaseValue);

    public override int GetHashCode() =>
        (SymbolEqualityComparer.Default.GetHashCode(Type) * 31) + Key.GetHashCode();

    public override string ToString() =>
        Type.ToDisplayString() + "." + Key;
}

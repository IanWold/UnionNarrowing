using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// A set of the values a value can hold, at the granularity the analysis works at:
/// whole case types of a union or closed hierarchy (or, for a bool or enum, its constant values; see <see cref="CaseSpace"/>), plus null.
/// </summary>
internal class CaseSet
{
    private static readonly ImmutableHashSet<object> NoCases = ImmutableHashSet.Create<object>(CaseComparer.Instance);

    public static readonly CaseSet Empty = new(NoCases, includesNull: false);

    public static readonly CaseSet Null = new(NoCases, includesNull: true);

    /// <summary>
    /// The non-null cases:
    /// type symbols, constants, or <see cref="CaseSpace"/>'s markers.
    /// </summary>
    public ImmutableHashSet<object> Cases { get; }

    public bool IncludesNull { get; }

    /// <summary>
    /// Whether the set holds nothing at all, not even null.
    /// </summary>
    public bool IsEmpty =>
        Cases.IsEmpty && !IncludesNull;

    /// <summary>
    /// The cases that are types.
    /// </summary>
    public IEnumerable<ITypeSymbol> Types =>
        Cases.OfType<ITypeSymbol>();

    private CaseSet(ImmutableHashSet<object> cases, bool includesNull)
    {
        Cases = cases;
        IncludesNull = includesNull;
    }

    public bool Contains(object candidate) =>
        Cases.Contains(candidate);

    /// <summary>
    /// Whether the set has the case, where a null case is null itself.
    /// </summary>
    public bool ContainsCase(object? candidate) =>
        candidate is null ? IncludesNull : Cases.Contains(candidate);

    public static CaseSet Of(IEnumerable<object> cases, bool includesNull = false) =>
        new(NoCases.Union(cases), includesNull);

    public override string ToString() =>
        "{" + string.Join(", ", Cases.Select(c => c is ISymbol symbol ? symbol.ToDisplayString() : c.ToString()).Concat(IncludesNull ? ["null"] : [])) + "}";

    public static CaseSet operator &(CaseSet left, CaseSet right) =>
        new(left.Cases.Intersect(right.Cases), left.IncludesNull && right.IncludesNull);

    public static CaseSet operator |(CaseSet left, CaseSet right) =>
        new(left.Cases.Union(right.Cases), left.IncludesNull || right.IncludesNull);
}

/// <summary>
/// How cases compare:
/// symbols as symbols, and constants and markers by value.
/// </summary>
internal class CaseComparer : IEqualityComparer<object>
{
    public static readonly CaseComparer Instance = new();

    public new bool Equals(object? x, object? y) =>
        x is ISymbol left && y is ISymbol right
            ? SymbolEqualityComparer.Default.Equals(left, right)
            : object.Equals(x, y);

    public int GetHashCode(object obj) =>
        obj is ISymbol symbol ? SymbolEqualityComparer.Default.GetHashCode(symbol) : obj.GetHashCode();
}

/// <summary>
/// What a pattern or condition says about each case:
/// the cases for which it's always true (it matches every value of the case) and the cases for which it's always false (it matches no value of the case).
/// Cases in neither set could go either way.
/// </summary>
internal class Outcome(CaseSet alwaysTrue, CaseSet alwaysFalse)
{
    public static readonly Outcome Unknown = new(CaseSet.Empty, CaseSet.Empty);

    public CaseSet AlwaysFalse { get; } = alwaysFalse;

    public CaseSet AlwaysTrue { get; } = alwaysTrue;

    public static Outcome And(Outcome left, Outcome right) =>
        new(left.AlwaysTrue & right.AlwaysTrue, left.AlwaysFalse | right.AlwaysFalse);

    public Outcome Not() =>
        new(AlwaysFalse, AlwaysTrue);

    public static Outcome Or(Outcome left, Outcome right) =>
        new(left.AlwaysTrue | right.AlwaysTrue, left.AlwaysFalse & right.AlwaysFalse);
}

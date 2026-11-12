using System;
using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// One step along the path from where a <see cref="TrackedValue"/> starts to the value:
/// a member read, an index, a step into the case a union holds, or any one element of a collection.
/// </summary>
internal abstract class Step : IEquatable<Step>
{
    private protected Step() { }

    public abstract bool Equals(Step? other);

    public sealed override bool Equals(object? obj) =>
        obj is Step other && Equals(other);

    public abstract override int GetHashCode();
}

/// <summary>
/// <c>.Pet</c>: a field or property.
/// A tuple element by name (<c>t.Pet</c>) is the same field as by position (<c>t.Item1</c>).
/// </summary>
internal class MemberStep(ISymbol member) : Step
{
    public ISymbol Member { get; } = member is IFieldSymbol field ? field.CorrespondingTupleField ?? field : member;

    /// <remarks>
    /// An instance member's object fixes its type, so the members' declarations are enough to tell paths from it apart.
    /// That makes a path read through a generic type's members (<c>q.Status</c> on a <c>Query&lt;string&gt;</c>) the same as the one its own members read (<c>Status</c> in <c>Query&lt;T&gt;</c>).
    /// A static member is told apart by its type's type arguments too: <c>Cache&lt;int&gt;.Pet</c> isn't <c>Cache&lt;string&gt;.Pet</c>.
    /// </remarks>
    public override bool Equals(Step? other) =>
        other is MemberStep step && SymbolEqualityComparer.Default.Equals(Identity, step.Identity);

    public override int GetHashCode() =>
        SymbolEqualityComparer.Default.GetHashCode(Identity);

    private ISymbol Identity =>
        Member.IsStatic ? Member : Member.OriginalDefinition;

    public override string ToString() =>
        Member.Name;
}

/// <summary>
/// <c>[i]</c>: the element at the index a local or parameter holds, which writing that variable changes.
/// </summary>
internal class IndexStep(ISymbol variable) : Step
{
    public ISymbol Variable { get; } = variable;

    public override bool Equals(Step? other) =>
        other is IndexStep step && SymbolEqualityComparer.Default.Equals(Variable, step.Variable);

    public override int GetHashCode() =>
        SymbolEqualityComparer.Default.GetHashCode(Variable) * 17;

    public override string ToString() =>
        "[" + Variable.Name + "]";
}

/// <summary>
/// <c>[0]</c>: the element at a constant index (see <see cref="CaseSpace.ConstantKey"/>), read through <paramref name="accessor"/>, the indexer (or <c>Array.GetValue</c>, for an array).
/// </summary>
internal class ConstantIndexStep(ISymbol accessor, object key) : Step
{
    public ISymbol Accessor { get; } = accessor;

    public object Key { get; } = key;

    public override bool Equals(Step? other) =>
        other is ConstantIndexStep step && SymbolEqualityComparer.Default.Equals(Accessor, step.Accessor) && Equals(Key, step.Key);

    public override int GetHashCode() =>
        (SymbolEqualityComparer.Default.GetHashCode(Accessor) * 31) + Key.GetHashCode();

    public override string ToString() =>
        "[" + Key + "]";
}

/// <summary>
/// A step into the case a union holds, as that case's type, or into an object as one of its closed hierarchy's types:
/// what's after it is only there when the value is one (see <see cref="NestedCoverage"/>).
/// </summary>
internal class CaseStep(ITypeSymbol type) : Step
{
    public ITypeSymbol Type { get; } = type;

    public override bool Equals(Step? other) =>
        other is CaseStep step && SymbolEqualityComparer.Default.Equals(Type, step.Type);

    public override int GetHashCode() =>
        SymbolEqualityComparer.Default.GetHashCode(Type) * 13;

    public override string ToString() =>
        "(" + Type.Name + ")";
}

/// <summary>
/// Any one element of a collection, as all of them are where something holds for every element.
/// </summary>
internal class ElementStep(ITypeSymbol elementType) : Step
{
    public ITypeSymbol ElementType { get; } = elementType;

    public override bool Equals(Step? other) =>
        other is ElementStep step && SymbolEqualityComparer.Default.Equals(ElementType, step.ElementType);

    public override int GetHashCode() =>
        SymbolEqualityComparer.Default.GetHashCode(ElementType) * 7;

    public override string ToString() =>
        "[element]";
}

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What generic code's type parameters stand for, where that code is read in place of a use of it:
/// <c>T</c> is <c>int</c> in the body of <c>Option&lt;T&gt; Some&lt;T&gt;(T value)</c>, read for the call <c>Some(1)</c>.
/// </summary>
internal sealed class TypeArguments
{
    private readonly ImmutableDictionary<ITypeParameterSymbol, ITypeSymbol> _arguments;

    /// <summary>
    /// For code read where it is, whose type parameters stand for themselves.
    /// </summary>
    public static readonly TypeArguments None = new(ImmutableDictionary.Create<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default));

    private TypeArguments(ImmutableDictionary<ITypeParameterSymbol, ITypeSymbol> arguments) =>
        _arguments = arguments;

    /// <summary>
    /// Whether the type is, or is made from, a type parameter (<c>T</c>, <c>Some&lt;T&gt;</c>, <c>T[]</c>):
    /// what it is depends on what that stands for.
    /// </summary>
    public static bool Mentioned(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => Mentioned(array.ElementType),
        IPointerTypeSymbol pointer => Mentioned(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(Mentioned) || (named.ContainingType is { } outer && Mentioned(outer)),
        _ => false,
    };

    /// <summary>
    /// The type with each type parameter that stands for something replaced by that.
    /// A type it can't rebuild is left as it is, still mentioning its type parameters.
    /// </summary>
    public ITypeSymbol Apply(ITypeSymbol type, Compilation compilation) =>
        _arguments.IsEmpty || !Mentioned(type)
            ? type
            : type switch
            {
                ITypeParameterSymbol parameter => _arguments.TryGetValue(parameter.OriginalDefinition, out var argument) ? argument : type,
                IArrayTypeSymbol array => compilation.CreateArrayTypeSymbol(Apply(array.ElementType, compilation), array.Rank),
                INamedTypeSymbol { IsGenericType: true, ContainingType: null or { IsGenericType: false } } named => named.ConstructedFrom.Construct([.. named.TypeArguments.Select(t => Apply(t, compilation))]),
                INamedTypeSymbol { ContainingType: { } outer } nested
                                when Apply(outer, compilation) is INamedTypeSymbol appliedOuter
                                && appliedOuter.GetTypeMembers(nested.Name, nested.Arity).FirstOrDefault() is { } member => nested.IsGenericType ? member.Construct([.. nested.TypeArguments.Select(t => Apply(t, compilation))]) : member,
                _ => type,
            };

    /// <summary>
    /// These, along with what the type parameters of <paramref name="member"/> and of the types it's in stand for where it's used (<c>T</c> for <c>Some&lt;int&gt;</c>, read through these, since the use can be in generic code being read too).
    /// </summary>
    public TypeArguments For(ISymbol member, Compilation compilation)
    {
        var arguments = _arguments.ToBuilder();

        void Add(ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> given)
        {
            for (var i = 0; i < parameters.Length && i < given.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(parameters[i], given[i]))
                {
                    arguments[parameters[i].OriginalDefinition] = Apply(given[i], compilation);
                }
            }
        }

        if (member is IMethodSymbol { IsGenericMethod: true, ReducedFrom: null } method)
        {
            Add(method.OriginalDefinition.TypeParameters, method.TypeArguments);
        }

        for (var type = member.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                Add(type.OriginalDefinition.TypeParameters, type.TypeArguments);
            }
        }

        var all = arguments.ToImmutable();
        return all == _arguments ? this : new TypeArguments(all);
    }
}

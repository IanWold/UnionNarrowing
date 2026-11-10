using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Recognizing well-known types, attributes and namespaces, and finding references to a symbol.
/// </summary>
internal static class Symbols
{
    /// <summary>
    /// Whether the symbol has the attribute, given by name and namespace parts (outermost first).
    /// </summary>
    public static bool HasAttribute(ISymbol symbol, string name, params string[] namespaceParts) =>
        symbol.GetAttributes().Any(attribute => IsAttribute(attribute, name, namespaceParts));

    /// <summary>
    /// Whether the type inherits from <c>System.<paramref name="systemName"/></c>.
    /// </summary>
    public static bool InheritsFrom(ITypeSymbol type, string systemName)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (Is(baseType, systemName, 0, "System"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the type, whatever its type arguments, is the top-level type <paramref name="name"/> with <paramref name="arity"/> type parameters, in the namespace given by its parts (outermost first).
    /// </summary>
    public static bool Is(ITypeSymbol? type, string name, int arity, params string[] namespaceParts) =>
        type?.OriginalDefinition is INamedTypeSymbol { ContainingType: null } named
        && named.Name == name
        && named.Arity == arity
        && IsNamespace(named.ContainingNamespace, namespaceParts);

    public static bool IsAttribute(AttributeData attribute, string name, params string[] namespaceParts) =>
        Is(attribute.AttributeClass, name, 0, namespaceParts);

    /// <summary>
    /// Whether the property's getter just reads its backing field:
    /// in source, it's declared as an auto-property (or is a record's positional property), going by the part with the accessors' bodies for a partial one;
    /// from another assembly, the compiler generated its getter.
    /// </summary>
    public static bool IsAutoProperty(IPropertySymbol property)
    {
        var implementation = property.PartialImplementationPart ?? property;
        return implementation.DeclaringSyntaxReferences.IsEmpty
            ? implementation.GetMethod is { } getter && IsCompilerGenerated(getter)
            : implementation.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() switch
            {
                PropertyDeclarationSyntax declaration => IsAutoPropertyDeclaration(declaration),
                ParameterSyntax => implementation.ContainingType.IsRecord,
                _ => false,
            });
    }

    /// <summary>
    /// Whether the declaration is of an auto-property:
    /// accessors, and none of them has a body.
    /// </summary>
    public static bool IsAutoPropertyDeclaration(PropertyDeclarationSyntax declaration) =>
        declaration is { ExpressionBody: null, AccessorList.Accessors: var accessors }
        && accessors.All(accessor => accessor is { Body: null, ExpressionBody: null });

    public static bool IsCompilerGenerated(ISymbol symbol) =>
        HasAttribute(symbol, "CompilerGeneratedAttribute", "System", "Runtime", "CompilerServices");

    public static bool IsImmutableArray(ITypeSymbol? type) =>
        Is(type, "ImmutableArray", 1, "System", "Collections", "Immutable");

    public static bool IsKeyValuePair(ITypeSymbol? type) =>
        Is(type, "KeyValuePair", 2, "System", "Collections", "Generic");

    public static bool IsList(ITypeSymbol? type) =>
        Is(type, "List", 1, "System", "Collections", "Generic");

    public static bool IsNamespace(INamespaceSymbol? symbol, params string[] parts)
    {
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (symbol is null || symbol.Name != parts[i])
            {
                return false;
            }

            symbol = symbol.ContainingNamespace;
        }

        return symbol is { IsGlobalNamespace: true };
    }

    /// <summary>
    /// Whether the type is one of the <c>System.Tuple</c> classes (not a value tuple).
    /// </summary>
    public static bool IsSystemTuple(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Tuple", Arity: > 0, IsReferenceType: true, ContainingType: null } tuple && IsNamespace(tuple.ContainingNamespace, "System");

    /// <summary>
    /// The identifiers in <paramref name="scope"/> that refer to <paramref name="symbol"/> (or, with <paramref name="byDefinition"/>, to the same definition, whatever the type arguments).
    /// </summary>
    /// <param name="descendInto">Which nodes to look inside, as for <c>DescendantNodes</c>;
    /// all of them if null.</param>
    public static IEnumerable<IdentifierNameSyntax> References(ISymbol symbol, SyntaxNode scope, SemanticModel model, CancellationToken ct, bool byDefinition = false, System.Func<SyntaxNode, bool>? descendInto = null) =>
        scope.DescendantNodesAndSelf(descendInto).OfType<IdentifierNameSyntax>().Where(name =>
            name.Identifier.ValueText == symbol.Name
            && (byDefinition
                ? SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol?.OriginalDefinition, symbol.OriginalDefinition)
                : SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, ct).Symbol, symbol)
            )
        );

    /// <summary>
    /// For <c>Nullable&lt;T&gt;</c>, T;
    /// null for any other type.
    /// </summary>
    public static ITypeSymbol? UnderlyingOfNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : null;
}

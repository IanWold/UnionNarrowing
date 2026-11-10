using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Patterns for the combinations of cases a switch misses:
/// one per case for a single input, and for a tuple, one per set of combinations that differ in one element only, with that element's cases joined by <c>or</c>, or <c>_</c> where they're all there.
/// They're written as text first, and parsed the way the compiler will read them.
/// </summary>
internal static class MissingPatterns
{
    /// <summary>
    /// Every case of a space, with null where it matters.
    /// </summary>
    private static List<object?> CasesOf(CaseSpace space) =>
        [.. space.Cases.Cast<object?>().Concat(space.NullMatters ? [null] : [])];

    /// <summary>
    /// How some of the cases of a tuple's element (by their indexes into <paramref name="all"/>, its cases) are written in its pattern:
    /// <c>_</c> for all of them;
    /// for some that include one only <c>_</c> can name (anything of a type the analysis doesn't list, or an enum's values no member has), <c>not</c> the others;
    /// otherwise each case, joined by <c>or</c>, with a union's case type named as itself where all of it is there.
    /// </summary>
    private static string Either(SortedSet<int> indexes, List<object?> all, CaseSpace space, SemanticModel model, int position)
    {
        if (indexes.Count == all.Count)
        {
            return "_";
        }

        var cases = indexes.Select(index => all[index]).ToList();
        if (cases.Any(candidate => candidate == CaseSpace.Anything || candidate == CaseSpace.OtherValues))
        {
            var others = all.Where((_, index) => !indexes.Contains(index)).ToList();
            return others.Count > 1 ? $"not ({Or(others, space, model, position)})" : $"not {Or(others, space, model, position)}";
        }

        return Or([.. space.Collapsed(cases.OfType<object>()), .. cases.Where(candidate => candidate is null)], space, model, position);
    }

    /// <summary>
    /// Rows of sets of cases, one set per element of a tuple, with any rows that differ in one element only merged into one, until no two do.
    /// The sets are indexes into each element's cases.
    /// </summary>
    private static List<SortedSet<int>[]> Merged(List<SortedSet<int>[]> rows, int elements)
    {
        for (var merging = true; merging;)
        {
            merging = false;
            for (var p = elements - 1; p >= 0; p--)
            {
                var at = p;
                var groups = rows.GroupBy(row => string.Join("|", row.Select((set, i) => i == at ? "" : string.Join(",", set)))).ToList();
                merging |= groups.Count < rows.Count;
                rows = [.. groups.Select(group =>
                {
                    var merged = (SortedSet<int>[])group.First().Clone();
                    merged[at] = [.. group.SelectMany(row => row[at])];
                    return merged;
                })];
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether <paramref name="name"/> means the type where an expression or a type pattern names it:
    /// a name the code in scope takes for a member, local or parameter instead (as <c>Error</c> does where there's an <c>Error Error</c> property) doesn't.
    /// </summary>
    private static bool Means(TypeSyntax name, ITypeSymbol type, SemanticModel model, int position) =>
        name is not NameSyntax
        || model.GetSpeculativeSymbolInfo(position, name, SpeculativeBindingOption.BindAsExpression).Symbol is ITypeSymbol bound
            && SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, type.OriginalDefinition);

    /// <summary>
    /// The cases, each as it's written in a pattern, joined by <c>or</c>.
    /// </summary>
    private static string Or(IEnumerable<object?> cases, CaseSpace space, SemanticModel model, int position) =>
        string.Join(" or ", cases.Select(candidate => Render(candidate, space, model, position)));

    /// <summary>
    /// The pattern the compiler reads in <paramref name="text"/>, as a switch arm's.
    /// </summary>
    private static PatternSyntax Pattern(string text) =>
        ((SwitchExpressionSyntax)ParseExpression($"_ switch {{ {text} => _ }}")).Arms[0].Pattern.WithoutTrivia();

    /// <summary>
    /// How a case is written in a pattern:
    /// a type's name, <c>null</c>, <c>true</c>, an enum member, or <c>_</c> for what can't be named (anything of a type the analysis doesn't list, or an enum value no member has).
    /// In a union, an enum case type's other values are written as the enum, which matches them all.
    /// </summary>
    private static string Render(object? candidate, CaseSpace space, SemanticModel model, int position)
    {
        switch (candidate)
        {
            case null:
                return "null";

            case ITypeSymbol type:
                return TypeName(type, model, position).ToString();

            case bool b:
                return b ? "true" : "false";

            case decimal value when space.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && Member(enumType, value) is { } member:
                return enumType.ToMinimalDisplayString(model, position) + "." + member.Name;

            // A value of a union's bool or enum case type.
            case CaseValue { Key: bool b }:
                return Render(b, space, model, position);

            case CaseValue { Type: INamedTypeSymbol { TypeKind: TypeKind.Enum } valueEnum, Key: decimal value } when Member(valueEnum, value) is { } valueMember:
                return valueEnum.ToMinimalDisplayString(model, position) + "." + valueMember.Name;

            case CaseValue { Type: var valueType }:
                return Render(valueType, space, model, position);

            default:
                return "_";
        }

        static IFieldSymbol? Member(INamedTypeSymbol enumType, decimal value) =>
            enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(CaseSpace.ConstantKey(f.ConstantValue), value));
    }

    /// <summary>
    /// Some of a space's cases (and null), each as it's written in a pattern, with a union's case type named as itself where all of it is there.
    /// </summary>
    private static IEnumerable<string> Rendered(IEnumerable<object> cases, bool withNull, CaseSpace space, SemanticModel model, int position) =>
        space.Collapsed(cases).Cast<object?>().Concat(withNull ? [null] : []).Select(candidate => Render(candidate, space, model, position));

    /// <summary>
    /// Patterns for some of a space's cases (and null), as <see cref="Rendered"/>.
    /// </summary>
    public static List<PatternSyntax> For(IEnumerable<object> cases, bool withNull, CaseSpace space, SemanticModel model, int position) =>
        [.. Rendered(cases, withNull, space, model, position).Select(Pattern)];

    /// <summary>
    /// How some of a space's cases are named in a message:
    /// <c>Dog, Bird</c>.
    /// </summary>
    public static string Names(IEnumerable<object> cases, CaseSpace space, SemanticModel model, int position) =>
        string.Join(", ", Rendered(cases, withNull: false, space, model, position));

    /// <summary>
    /// Patterns for the combinations of cases a switch misses.
    /// </summary>
    public static List<PatternSyntax> Of(SwitchCoverage coverage, SemanticModel model, int position) =>
        Of(coverage.Spaces, coverage.Missing, model, position);

    /// <summary>
    /// Patterns for combinations of cases, one per input, of inputs with these case spaces.
    /// </summary>
    public static List<PatternSyntax> Of(IReadOnlyList<CaseSpace> spaces, IReadOnlyList<object?[]> combinations, SemanticModel model, int position)
    {
        // One input: each case, with a union's case type named as itself where all of it is missing.
        if (spaces.Count == 1)
        {
            var missing = combinations.Select(combination => combination[0]).ToList();
            return For(missing.OfType<object>(), missing.Contains(null), spaces[0], model, position);
        }

        var cases = spaces.Select(CasesOf).ToList();
        var rows = combinations
            .Select(combination => combination.Select((candidate, i) => new SortedSet<int> { cases[i].FindIndex(c => CaseComparer.Instance.Equals(c, candidate)) }).ToArray())
            .ToList();

        return [.. Merged(rows, spaces.Count).Select(row => Pattern($"({string.Join(", ", row.Select((indexes, i) => Either(indexes, cases[i], spaces[i], model, position)))})"))];
    }

    /// <summary>
    /// How a case is written as a pattern on its own.
    /// </summary>
    public static PatternSyntax Of(object? candidate, CaseSpace space, SemanticModel model, int position) =>
        Pattern(Render(candidate, space, model, position));

    /// <summary>
    /// How a type pattern names a type:
    /// as briefly as still <see cref="Means">means</see> the type there;
    /// a tuple as <c>ValueTuple&lt;...&gt;</c>, since <c>(int, string)</c> would be a positional pattern;
    /// and for a nullable value type <c>T?</c>, which a pattern can't name, T, which is what a value of it holds.
    /// </summary>
    public static TypeSyntax TypeName(ITypeSymbol type, SemanticModel model, int position)
    {
        type = Symbols.UnderlyingOfNullable(type) ?? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var name = type is INamedTypeSymbol { IsTupleType: true } tuple
            ? $"ValueTuple<{string.Join(", ", tuple.TypeArguments.Select(argument => argument.ToMinimalDisplayString(model, position)))}>"
            : type.ToMinimalDisplayString(model, position);

        string[] qualified = type.ContainingSymbol switch
        {
            INamespaceSymbol { IsGlobalNamespace: true } => ["global::" + name],
            INamespaceOrTypeSymbol container => [container.ToMinimalDisplayString(model, position) + "." + name, container.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + name],
            _ => [],
        };

        return new[] { name }.Concat(qualified).Select(candidate => ParseTypeName(candidate)).FirstOrDefault(candidate => Means(candidate, type, model, position))
            ?? ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }
}

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Patterns for the combinations of cases a switch misses:
/// one per case for a single input, and for a tuple, one per combination, with a <c>_</c> wherever every case of an element is missing for the same rest.
/// </summary>
internal static class MissingPatterns
{
    /// <summary>
    /// A case written as (part of) a pattern, with its text to tell them apart by.
    /// </summary>
    private readonly struct Element(string text, PatternSyntax pattern)
    {
        public PatternSyntax Pattern { get; } = pattern;

        public string Text { get; } = text;
    }

    private static readonly Element Discard = new("_", DiscardPattern());

    /// <summary>
    /// How a case is written in a pattern:
    /// a type's name, <c>null</c>, <c>true</c>, an enum member, or <c>_</c> for what can't be named (anything of a type the analysis doesn't list, or an enum value no member has).
    /// In a union, an enum case type's other values are written as the enum, which matches them all.
    /// </summary>
    private static Element Render(object? candidate, CaseSpace space, SemanticModel model, int position)
    {
        switch (candidate)
        {
            case null:
                return new("null", ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression)));

            case ITypeSymbol type:
                var name = type.ToMinimalDisplayString(model, position);
                return new(name, TypePattern(ParseTypeName(name)));

            case bool b:
                return new(b ? "true" : "false", ConstantPattern(LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression)));

            case decimal value when space.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && Member(enumType, value) is { } member:
                var text = enumType.ToMinimalDisplayString(model, position) + "." + member.Name;
                return new(text, ConstantPattern(ParseExpression(text)));

            // A value of a union's bool or enum case type.
            case CaseValue { Key: bool b }:
                return Render(b, space, model, position);

            case CaseValue { Type: INamedTypeSymbol { TypeKind: TypeKind.Enum } valueEnum, Key: decimal value } when Member(valueEnum, value) is { } valueMember:
                var valueText = valueEnum.ToMinimalDisplayString(model, position) + "." + valueMember.Name;
                return new(valueText, ConstantPattern(ParseExpression(valueText)));

            case CaseValue { Type: var valueType }:
                return Render(valueType, space, model, position);

            default:
                return Discard;
        }

        static IFieldSymbol? Member(INamedTypeSymbol enumType, decimal value) =>
            enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(CaseSpace.ConstantKey(f.ConstantValue), value));
    }

    /// <summary>
    /// Patterns for some of a space's cases (and null), with a union's case type named as itself where all of it is there.
    /// </summary>
    public static List<PatternSyntax> For(IEnumerable<object> cases, bool withNull, CaseSpace space, SemanticModel model, int position) =>
        [.. space.Collapsed(cases).Cast<object?>().Concat(withNull ? [null] : []).Select(candidate => Render(candidate, space, model, position).Pattern)];

    /// <summary>
    /// How some of a space's cases are named in a message:
    /// <c>Dog, Bird</c>.
    /// </summary>
    public static string Names(IEnumerable<object> cases, CaseSpace space, SemanticModel model, int position) =>
        string.Join(", ", space.Collapsed(cases).Select(candidate => Render(candidate, space, model, position).Text));

    public static List<PatternSyntax> Of(SwitchCoverage coverage, SemanticModel model, int position)
    {
        var spaces = coverage.Spaces;

        // One input: each case, with a union's case type named as itself where all of it is missing.
        if (spaces.Count == 1)
        {
            var missing = coverage.Missing.Select(combination => combination[0]).ToList();
            return [.. spaces[0].Collapsed(missing.OfType<object>()).Cast<object?>().Concat(missing.Contains(null) ? [null] : []).Select(candidate => Render(candidate, spaces[0], model, position).Pattern.NormalizeWhitespace())];
        }

        var rows = coverage.Missing.Select(combination => combination.Select((candidate, i) => Render(candidate, spaces[i], model, position)).ToArray()).ToList();

        // From the last element back: where the rows that agree on every other element have every case of this one,
        // they become one row with `_` there.
        for (var p = spaces.Count - 1; p >= 0; p--)
        {
            var all = spaces[p].Cases.Cast<object?>().Concat(spaces[p].NullMatters ? [null] : []).Select(candidate => Render(candidate, spaces[p], model, position).Text).Distinct().ToList();
            var at = p;

            rows = [.. rows
                .GroupBy(row => Key(row.Where((_, i) => i != at)))
                .SelectMany(group =>
                {
                    var values = group.Select(row => row[at].Text).Distinct().ToList();

                    if (!all.All(values.Contains))
                    {
                        return group.AsEnumerable();
                    }

                    var merged = (Element[])group.First().Clone();
                    merged[at] = Discard;
                    
                    return [merged];
                })
            ];
        }

        return [.. rows
            .GroupBy(Key)
            .Select(group => (PatternSyntax)RecursivePattern(
                type: null,
                PositionalPatternClause(SeparatedList(group.First().Select(element => Subpattern(element.Pattern)))),
                propertyPatternClause: null,
                designation: null
            ).NormalizeWhitespace()
            )];

        static string Key(IEnumerable<Element> row) =>
            string.Join("|", row.Select(e => e.Text));
    }

    /// <summary>
    /// How a case is written as a pattern on its own.
    /// </summary>
    public static PatternSyntax Of(object? candidate, CaseSpace space, SemanticModel model, int position) =>
        Render(candidate, space, model, position).Pattern;
}

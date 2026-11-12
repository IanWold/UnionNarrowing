using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Looks at what a union holds instead of the boxed union (UN0014):
/// <c>(object)pet is Cat</c> becomes <c>pet is Cat</c>, <c>(object)pet as Cat</c> becomes <c>pet.Value as Cat</c>, <c>(Cat)(object)pet</c> becomes <c>(Cat)pet.Value!</c>, <c>pet.Equals(cat)</c> becomes <c>object.Equals(pet.Value, cat)</c> (in a conditional access, <c>owner?.Pet.Value?.Equals(cat)</c>), and <c>pet.GetType()</c> becomes <c>pet.Value?.GetType()</c>.
/// Not offered where that doesn't compile: in a lambda that's an expression tree, which can't have <c>?.</c>, or a pattern on a union (<c>pet is Cat</c>).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BoxedUnionCodeFix)), Shared]
public sealed class BoxedUnionCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.BoxedUnionTest.Id];

    private static ExpressionSyntax? Replacement(ExpressionSyntax expression)
    {
        switch (expression)
        {
            // `(object)pet is Cat`, `(object)pet is Cat c`
            case IsPatternExpressionSyntax { Expression: var boxed } isPattern when Unboxed(boxed) is { } union:
                return isPattern.WithExpression(union.WithTriviaFrom(boxed));
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Left: var boxed } isType when Unboxed(boxed) is { } union:
                return isType.WithLeft(union.WithTriviaFrom(boxed));

            // `(object)pet as Cat`
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression, Left: var boxed } asType when Unboxed(boxed) is { } union:
                return asType.WithLeft(CodeFixSyntax.ValueOf(union).WithTriviaFrom(boxed));

            // `(Cat)(object)pet`
            case CastExpressionSyntax { Expression: var boxed } cast when Unboxed(boxed) is { } union:
                return cast.WithExpression(PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, CodeFixSyntax.ValueOf(union)));

            // `pet.Equals(cat)`, or in a conditional access, `owner?.Pet.Equals(cat)`
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" } equals, ArgumentList.Arguments: { Count: 1 } arguments } call:
                return CodeFixSyntax.InConditionalAccess(call)
                    ? ConditionalAccessExpression(CodeFixSyntax.ValueOf(equals.Expression), call.WithExpression(MemberBindingExpression(equals.Name)))
                    : InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, PredefinedType(Token(SyntaxKind.ObjectKeyword)), IdentifierName("Equals")),
                        ArgumentList(SeparatedList([Argument(CodeFixSyntax.ValueOf(equals.Expression)), arguments[0]]))
                    );

            // `pet.GetType() == typeof(Cat)`
            case BinaryExpressionSyntax comparison when comparison.IsKind(SyntaxKind.EqualsExpression) || comparison.IsKind(SyntaxKind.NotEqualsExpression):
                var getType = new[] { comparison.Left, comparison.Right }.OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => i is { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetType" }, ArgumentList.Arguments.Count: 0 });
                if (getType?.Expression is not MemberAccessExpressionSyntax access)
                {
                    return null;
                }

                var valueType = ConditionalAccessExpression(CodeFixSyntax.ValueOf(access.Expression), InvocationExpression(MemberBindingExpression(IdentifierName("GetType"))));
                return comparison.ReplaceNode(getType, valueType.WithTriviaFrom(getType));

            default:
                return null;
        }
    }

    /// <summary>
    /// For <c>(object)x</c> (in any parentheses), x.
    /// </summary>
    private static ExpressionSyntax? Unboxed(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression is CastExpressionSyntax { Expression: var inner } ? inner.WithoutTrivia() : null;
    }

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var found = root?.FindNode(context.Span, getInnermostNodeForTie: true);

        // An arm or label of a switch on a boxed union: unbox the switch's input. (`case Cat:` is a constant label as
        // far as the syntax goes, not a pattern.)
        if (found?.AncestorsAndSelf().FirstOrDefault(n => n.Span == context.Span && n is PatternSyntax or ExpressionSyntax { Parent: CaseSwitchLabelSyntax }) is { } armPattern
            && armPattern.Ancestors().Select(a => a switch { SwitchExpressionSyntax e => e.GoverningExpression, SwitchStatementSyntax s => s.Expression, _ => null }).FirstOrDefault(g => g is not null) is { } governing
            && Unboxed(governing) is { } unboxed
        )
        {
            context.RegisterCodeFix(
                CodeAction.Create("Switch on the union", async ct =>
                {
                    var current = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    return context.Document.WithSyntaxRoot(current!.ReplaceNode(governing, unboxed.WithTriviaFrom(governing)));
                }, equivalenceKey: nameof(BoxedUnionCodeFix)
                ),
                context.Diagnostics
            );
            return;
        }

        if (found is not ExpressionSyntax node)
        {
            return;
        }

        var expression = node.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault(e => e.Span == context.Span) ?? node;
        if (Replacement(expression) is not { } replacement
            || !await CodeFixSyntax.CompilesAsync(context.Document, expression, replacement.WithTriviaFrom(expression), context.CancellationToken).ConfigureAwait(false)
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Look at what the union holds", async ct =>
            {
                var current = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                return context.Document.WithSyntaxRoot(current!.ReplaceNode(expression, replacement.WithTriviaFrom(expression)));
            }, equivalenceKey: nameof(BoxedUnionCodeFix)
            ),
            context.Diagnostics
        );
    }
}

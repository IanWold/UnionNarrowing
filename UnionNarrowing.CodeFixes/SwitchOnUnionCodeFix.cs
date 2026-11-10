using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Switches on, or tests, a union rather than its Value (UN0005).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SwitchOnUnionCodeFix)), Shared]
public sealed class SwitchOnUnionCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.SwitchOnValue.Id];

    private static async Task<Document> FixAsync(Document document, MemberAccessExpressionSyntax access, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        return document.WithSyntaxRoot(root!.ReplaceNode(access, access.Expression.WithTriviaFrom(access)));
    }

    /// <summary>
    /// Whether the Value is tested with <c>is</c>, rather than switched on.
    /// </summary>
    private static bool IsTested(ExpressionSyntax value)
    {
        var outer = value.Parent;
        while (outer is ParenthesizedExpressionSyntax)
        {
            outer = outer.Parent;
        }

        return outer is IsPatternExpressionSyntax || outer.IsKind(SyntaxKind.IsExpression);
    }

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span, getInnermostNodeForTie: true) is not MemberAccessExpressionSyntax access)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create($"{(IsTested(access) ? "Test" : "Switch on")} '{access.Expression}'", ct => FixAsync(context.Document, access, ct), equivalenceKey: nameof(SwitchOnUnionCodeFix)),
            context.Diagnostics
        );
    }
}

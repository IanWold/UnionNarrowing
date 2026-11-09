using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Switches on a union rather than its Value (UN0005).
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
            CodeAction.Create($"Switch on '{access.Expression}'", ct => FixAsync(context.Document, access, ct), equivalenceKey: nameof(SwitchOnUnionCodeFix)),
            context.Diagnostics
        );
    }
}

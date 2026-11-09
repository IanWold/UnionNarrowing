using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using UnionNarrowing.Suppressors.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// A switch statement over a union or closed hierarchy that handles every case (or every case earlier code leaves) still lets control fall out of its end, as far as definite assignment goes:
/// "not all code paths return a value" (CS0161, or CS1643 in a lambda) after one at the end of a method, and "use of unassigned local variable" (CS0165) after one that assigns in every section.
/// These are errors, so nothing can suppress them;
/// this adds the throwing <c>default:</c> section they need.
/// (UN0002 then reports any case added later that ends up there.)
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddThrowingDefaultCodeFix)), Shared]
public sealed class AddThrowingDefaultCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["CS0161", "CS1643", "CS0165"];

    private static async Task<Document> AddDefaultAsync(Document document, SwitchStatementSyntax switchStatement, string exception, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var section = SwitchSection(
            SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()),
            SingletonList<StatementSyntax>(ThrowStatement(CodeFixSyntax.NewException(exception)))
        )
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newDocument = document.WithSyntaxRoot(root!.ReplaceNode(switchStatement, switchStatement.AddSections(section)));
        newDocument = await Simplifier.ReduceAsync(newDocument, Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For CS0161 on a method (or local function, or accessor):
    /// its body.
    /// </summary>
    private static BlockSyntax? BodyOf(SyntaxNode node) =>
        node.AncestorsAndSelf().Select(n => n switch
        {
            BaseMethodDeclarationSyntax method => method.Body,
            LocalFunctionStatementSyntax local => local.Body,
            AccessorDeclarationSyntax accessor => accessor.Body,
            AnonymousFunctionExpressionSyntax lambda => lambda.Block,
            _ => null,
        }).FirstOrDefault(b => b is not null);

    /// <summary>
    /// For CS0161: the switch statement the body ends with.
    /// </summary>
    private static SwitchStatementSyntax? LastSwitchIn(SyntaxNode node) =>
        BodyOf(node)?.Statements.LastOrDefault() as SwitchStatementSyntax;

    /// <summary>
    /// For CS0165 on a read of a local:
    /// the switch statement just before the statement the read is in.
    /// </summary>
    private static SwitchStatementSyntax? SwitchBefore(SyntaxNode node)
    {
        foreach (var statement in node.AncestorsAndSelf().OfType<StatementSyntax>())
        {
            if (statement.Parent is BlockSyntax block)
            {
                var index = block.Statements.IndexOf(statement);
                return index > 0 ? block.Statements[index - 1] as SwitchStatementSyntax : null;
            }
        }

        return null;
    }

    private static async Task<Document> ThrowAtEndAsync(Document document, BlockSyntax body, string exception, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var thrown = ThrowStatement(CodeFixSyntax.NewException(exception)).WithAdditionalAnnotations(Formatter.Annotation);
        var newDocument = document.WithSyntaxRoot(root!.ReplaceNode(body, body.AddStatements(thrown)));
        newDocument = await Simplifier.ReduceAsync(newDocument, Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var ct = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        var session = new Session(context.Document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);

        var node = root.FindNode(context.Span, getInnermostNodeForTie: true);
        var exception = model.Compilation.GetTypeByMetadataName("System.Diagnostics.UnreachableException") is not null
            ? "System.Diagnostics.UnreachableException"
            : "System.InvalidOperationException";

        // A method that ends in ifs that test every case and return (`if (pet is Cat) return 1; ... if (pet is Bird) return 3;`).
        if (context.Diagnostics[0].Id is "CS0161" or "CS1643" && BodyOf(node) is { Statements.Count: > 0 } body
            && Handling.IfRunBefore(body.Statements, body.Statements.Count, model, session, ct) is { } handling
            && handling.Caught(handling.Excluded(model, ct)).Count == 0
        )
        {
            context.RegisterCodeFix(
                CodeAction.Create("Throw after the ifs", ct => ThrowAtEndAsync(context.Document, body, exception, ct), equivalenceKey: nameof(AddThrowingDefaultCodeFix)),
                context.Diagnostics
            );
            return;
        }

        var candidate = context.Diagnostics[0].Id is "CS0161" or "CS1643" ? LastSwitchIn(node) : SwitchBefore(node);
        if (candidate is null
            || candidate.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax))
            || SwitchCoverage.Of(candidate, model, session, ct, countGuarded: false) is not { OverCases: true, Complete: true }
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Add a throwing default section", ct => AddDefaultAsync(context.Document, candidate, exception, ct), equivalenceKey: nameof(AddThrowingDefaultCodeFix)),
            context.Diagnostics
        );
    }
}

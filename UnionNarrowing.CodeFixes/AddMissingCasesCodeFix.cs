using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using UnionNarrowing.Suppressors.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For the IDE's "populate switch" suggestion (IDE0010) on a switch statement over a union or closed hierarchy:
/// adds a <c>case</c> section for each case it doesn't handle, leaving out cases earlier code already ruled out.
/// Each new section throws <see cref="System.NotImplementedException"/> until it's filled in.
/// (The IDE's own fix adds a throwing default, which would catch cases added later too.)
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingCasesCodeFix)), Shared]
public sealed class AddMissingCasesCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["IDE0010"];

    private static async Task<Document> AddCasesAsync(Document document, SwitchStatementSyntax switchStatement, IReadOnlyList<PatternSyntax> patterns, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var sections = patterns.Select(pattern => SwitchSection(
            SingletonList<SwitchLabelSyntax>(CasePatternSwitchLabel(pattern, Token(SyntaxKind.ColonToken))),
            SingletonList<StatementSyntax>(ThrowStatement(CodeFixSyntax.NewException("System.NotImplementedException")))
        )
            .WithAdditionalAnnotations(Formatter.Annotation)
        ).ToList();

        var existing = switchStatement.Sections;
        var defaultIndex = existing.IndexOf(section => section.Labels.Any(label => label is DefaultSwitchLabelSyntax));
        var updated = defaultIndex >= 0 ? existing.InsertRange(defaultIndex, sections) : existing.AddRange(sections);

        var newDocument = document.WithSyntaxRoot(root!.ReplaceNode(switchStatement, switchStatement.WithSections(updated)));
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
        if (root.FindToken(context.Span.Start).Parent?.AncestorsAndSelf().OfType<SwitchStatementSyntax>().FirstOrDefault() is not { } switchStatement)
        {
            return;
        }

        var coverage = SwitchCoverage.Of(switchStatement, model, session, ct, countGuarded: false);
        if (!coverage.OverCases || coverage.Missing.Count == 0)
        {
            return;
        }

        var patterns = MissingPatterns.Of(coverage, model, switchStatement.SpanStart);
        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add cases for {string.Join(", ", patterns.Select(p => p.ToString()))}",
                ct => AddCasesAsync(context.Document, switchStatement, patterns, ct),
                equivalenceKey: nameof(AddMissingCasesCodeFix)
            ),
            context.Diagnostics
        );
    }
}

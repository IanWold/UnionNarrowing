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
using UnionNarrowing.Suppressors.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For the IDE's "populate switch" suggestion (IDE0010) on a switch statement over a union or closed hierarchy:
/// adds a <c>case</c> section for each case it doesn't handle, leaving out cases earlier code already ruled out.
/// Each new section throws <see cref="System.NotImplementedException"/> until it's filled in.
/// (The IDE's own fix adds a throwing default, which would catch cases added later too.)
/// </summary>
/// <remarks>
/// Fix All adds the sections to every switch in one pass, and a using for the exception, where the file needs one, once:
/// merged, what each fix does on its own would add the using once per fix, and the copies collide.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingCasesCodeFix)), Shared]
public sealed class AddMissingCasesCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["IDE0010"];

    private static async Task<Document> AddCasesAsync(Document document, IReadOnlyDictionary<SwitchStatementSyntax, List<PatternSyntax>> missing, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var newRoot = root!.ReplaceNodes(missing.Keys, (original, current) => WithSections(current, missing[original]));
        var newDocument = await CodeFixSyntax.ShortenNamesAsync(document.WithSyntaxRoot(newRoot), ct).ConfigureAwait(false);
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For each switch statement the diagnostics are on, and the fix is offered for, patterns for what it misses.
    /// </summary>
    private static async Task<Dictionary<SwitchStatementSyntax, List<PatternSyntax>>> MissingAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken ct)
    {
        var missing = new Dictionary<SwitchStatementSyntax, List<PatternSyntax>>();
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        
        if (root is null || model is null)
        {
            return missing;
        }

        var session = new Session(document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        foreach (var diagnostic in diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?.AncestorsAndSelf().OfType<SwitchStatementSyntax>().FirstOrDefault() is { } switchStatement
                && !missing.ContainsKey(switchStatement)
                && SwitchCoverage.Of(switchStatement, model, session, ct, countGuarded: false) is { OverCases: true, Missing.Count: > 0 } coverage
            )
            {
                missing[switchStatement] = MissingPatterns.Of(coverage, model, switchStatement.SpanStart);
            }
        }

        return missing;
    }

    /// <summary>
    /// The switch statement with a section for each case, before its default section if it has one.
    /// </summary>
    private static SwitchStatementSyntax WithSections(SwitchStatementSyntax switchStatement, IReadOnlyList<PatternSyntax> cases)
    {
        var sections = cases.Select(pattern => SwitchSection(
            SingletonList<SwitchLabelSyntax>(CasePatternSwitchLabel(pattern, Token(SyntaxKind.ColonToken))),
            SingletonList<StatementSyntax>(ThrowStatement(CodeFixSyntax.NewException("System.NotImplementedException")))
        )
            .WithAdditionalAnnotations(Formatter.Annotation)
        ).ToList();

        var existing = switchStatement.Sections;
        var defaultIndex = existing.IndexOf(section => section.Labels.Any(label => label is DefaultSwitchLabelSyntax));
        return switchStatement.WithSections(defaultIndex >= 0 ? existing.InsertRange(defaultIndex, sections) : existing.AddRange(sections));
    }

    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) =>
            await AddCasesAsync(document, await MissingAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false)
        );

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var missing = await MissingAsync(context.Document, context.Diagnostics, context.CancellationToken).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add cases for {string.Join(", ", missing.Values.SelectMany(patterns => patterns))}",
                ct => AddCasesAsync(context.Document, missing, ct),
                equivalenceKey: nameof(AddMissingCasesCodeFix)
            ),
            context.Diagnostics
        );
    }
}

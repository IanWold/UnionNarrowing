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
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For a throwing default that catches cases (UN0002):
/// names those cases instead.
/// In a switch expression, <c>_ =&gt; throw ...</c> becomes <c>Dog or Bird =&gt; throw ...</c>, so the compiler reports a case added later as missing.
/// In a switch statement, the default section gets <c>case</c> labels for them, and keeps its <c>default:</c> (which definite return still needs), where this analyzer will report a case added later.
/// For a throw after a switch statement, a section for them is added that throws the same way;
/// for a throw after a chain of ifs, one more if, for them, before it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ListCaughtCasesCodeFix)), Shared]
public sealed class ListCaughtCasesCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.ThrowingDefault.Id];

    /// <summary>
    /// The cases the default catches, as patterns:
    /// not handled before it, nor ruled out by earlier code, with null where it matters.
    /// </summary>
    private static List<PatternSyntax> Caught(Handling handling, SemanticModel model, CancellationToken ct)
    {
        var excluded = handling.Excluded(model, ct);
        var caught = handling.Caught(excluded);
        var withNull = handling.Space.NullMatters && !handling.Handled.IncludesNull && !excluded.IncludesNull;
        return caught.Count == 0 ? [] : MissingPatterns.For(caught, withNull, handling.Space, model, handling.Start.SpanStart);
    }

    private static PatternSyntax Either(IReadOnlyList<PatternSyntax> patterns) =>
        patterns.Skip(1).Aggregate(patterns[0], (left, right) => BinaryPattern(SyntaxKind.OrPattern, left, right));

    /// <summary>
    /// At the end of a chain of ifs, one more <c>else if</c> for the cases, that does what the else does:
    /// <c>else throw ...;</c> becomes <c>else if (pet is Dog or Bird) throw ...; else throw ...;</c>.
    /// The else then only catches a case added later, which UN0002 reports.
    /// The else keeps its own layout, braces and line breaks included.
    /// </summary>
    private static async Task<Document> NameBeforeElseAsync(Document document, ElseClauseSyntax elseClause, IOperation input, IReadOnlyList<PatternSyntax> caught, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var elseKeyword = elseClause.ElseKeyword;
        var statement = elseClause.Statement;
        var test = IfStatement(
            Token(SyntaxKind.IfKeyword).WithTrailingTrivia(Space),
            Token(SyntaxKind.OpenParenToken),
            IsPatternExpression(((ExpressionSyntax)input.Syntax).WithoutTrivia(), Either(caught)).WithAdditionalAnnotations(Formatter.Annotation),
            Token(SyntaxKind.CloseParenToken).WithTrailingTrivia(elseKeyword.TrailingTrivia),
            statement,
            ElseClause(elseKeyword, statement)
        );
        var newRoot = root!.ReplaceNode(elseClause, elseClause.WithElseKeyword(elseKeyword.WithTrailingTrivia(Space)).WithStatement(test));
        return await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a switch statement:
    /// a section for the cases, throwing the same way.
    /// After ifs: one more if.
    /// </summary>
    private static async Task<Document> NameBeforeThrowAsync(Document document, ThrowStatementSyntax throwStatement, SyntaxNode first, IOperation input, IReadOnlyList<PatternSyntax> caught, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var thrown = throwStatement.WithoutTrivia().WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newRoot;
        if (first is SwitchStatementSyntax switchStatement)
        {
            var labels = caught.Select(pattern => (SwitchLabelSyntax)CasePatternSwitchLabel(pattern, Token(SyntaxKind.ColonToken)));
            var section = SwitchSection(List(labels), SingletonList<StatementSyntax>(thrown)).WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root!.ReplaceNode(switchStatement, switchStatement.AddSections(section));
        }
        else
        {
            var test = IfStatement(IsPatternExpression(((ExpressionSyntax)input.Syntax).WithoutTrivia(), Either(caught)), thrown)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var lineBreak = throwStatement.GetFirstToken().GetPreviousToken().TrailingTrivia;
            newRoot = root!.InsertNodesBefore(throwStatement, [test.WithLeadingTrivia(throwStatement.GetLeadingTrivia()).WithTrailingTrivia(lineBreak)]);
        }

        return await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task<Document> NameInArmAsync(Document document, SwitchExpressionSyntax switchExpr, IReadOnlyList<PatternSyntax> caught, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var last = switchExpr.Arms[switchExpr.Arms.Count - 1];

        // `var x => throw new E(x)` keeps x: `(Dog or Bird) and var x`.
        PatternSyntax pattern = Either(caught);
        if (last.Pattern is VarPatternSyntax or DeclarationPatternSyntax)
        {
            pattern = BinaryPattern(SyntaxKind.AndPattern, caught.Count > 1 ? ParenthesizedPattern(pattern) : pattern, last.Pattern);
        }

        var newArm = last.WithPattern(pattern.WithTriviaFrom(last.Pattern));
        return document.WithSyntaxRoot(root!.ReplaceNode(last, newArm));
    }

    private static async Task<Document> NameInSectionAsync(Document document, SwitchStatementSyntax switchStatement, IReadOnlyList<PatternSyntax> caught, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var section = switchStatement.Sections.First(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        var labels = caught.Select(pattern => (SwitchLabelSyntax)CasePatternSwitchLabel(pattern, Token(SyntaxKind.ColonToken)).WithAdditionalAnnotations(Formatter.Annotation));
        var defaultIndex = section.Labels.IndexOf(l => l is DefaultSwitchLabelSyntax);
        var newSection = section.WithLabels(section.Labels.InsertRange(defaultIndex, labels));
        var newDocument = document.WithSyntaxRoot(root!.ReplaceNode(section, newSection));
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string Names(IReadOnlyList<PatternSyntax> caught) =>
        string.Join(", ", caught.Select(c => c.ToString()));

    /// <summary>
    /// The first if of the <c>if</c>/<c>else if</c> chain an else belongs to.
    /// </summary>
    private static IfStatementSyntax? TopOfChain(ElseClauseSyntax elseClause)
    {
        if (elseClause.Parent is not IfStatementSyntax ifStatement)
        {
            return null;
        }

        while (ifStatement.Parent is ElseClauseSyntax { Parent: IfStatementSyntax outer })
        {
            ifStatement = outer;
        }

        return ifStatement;
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

        switch (root.FindToken(context.Span.Start).Parent)
        {
            // Over a tuple: the combinations, as the patterns Add missing arms writes.
            case SwitchExpressionSyntax { Arms.Count: > 1, GoverningExpression: TupleExpressionSyntax } tupleSwitch
                when SwitchCoverage.Of(tupleSwitch, model, session, ct, countGuarded: false, withoutDefault: true) is { OverCases: true, Missing.Count: > 0 } coverage:
                var combinations = MissingPatterns.Of(coverage, model, tupleSwitch.SpanStart);
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(combinations)} in the throwing arm", ct => NameInArmAsync(context.Document, tupleSwitch, combinations, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchExpressionSyntax { Arms.Count: > 1 } switchExpr when Handling.BeforeDefaultOf(switchExpr, model, session, ct) is { } handling
                && Caught(handling, model, ct) is { Count: > 0 } caught:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caught)} in the throwing arm", ct => NameInArmAsync(context.Document, switchExpr, caught, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case ThrowStatementSyntax throwStatement when Handling.BeforeThrow(throwStatement, model, session, ct) is { } after
                && Caught(after, model, ct) is { Count: > 0 } caughtAfter:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caughtAfter)} before the throw", ct => NameBeforeThrowAsync(context.Document, throwStatement, after.Start, after.Input, caughtAfter, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            // The else that ends a chain of ifs (UN0002 is on its keyword).
            case ElseClauseSyntax elseClause when TopOfChain(elseClause) is { } top
                && model.GetOperation(top, ct) is IConditionalOperation topOp
                && Handling.BeforeThrowingElse(topOp, model, session, ct) is { } chain && chain.Else == elseClause
                && Caught(chain.Handling, model, ct) is { Count: > 0 } caughtByElse:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caughtByElse)} before the else", ct => NameBeforeElseAsync(context.Document, elseClause, chain.Handling.Input, caughtByElse, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchStatementSyntax switchStatement when Handling.BeforeDefaultOf(switchStatement, model, session, ct) is { } handling
                && Caught(handling, model, ct) is { Count: > 0 } caught:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caught)} in the default section", ct => NameInSectionAsync(context.Document, switchStatement, caught, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;
        }
    }
}

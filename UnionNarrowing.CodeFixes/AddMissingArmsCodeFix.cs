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
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Adds an arm for each case a switch over a union or closed hierarchy doesn't handle (CS8509), leaving out cases earlier code already ruled out.
/// Each new arm throws <see cref="System.NotImplementedException"/> until it's filled in.
/// Also for CS8846, which is CS8509 when an arm with a <c>when</c> clause might cover what's missing, and for CS8655 and CS8847, the same two for null alone, where the arm it adds is the <c>null</c> arm.
/// Not offered for a switch with arms in an <c>#if</c> section that isn't compiled, which could handle what it would add.
/// </summary>
/// <remarks>
/// Fix All adds the arms to every switch in one pass, and a using for the exception, where the file needs one, once:
/// merged, what each fix does on its own would add the using once per fix, and the copies collide.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingArmsCodeFix)), Shared]
public sealed class AddMissingArmsCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["CS8509", "CS8846", "CS8655", "CS8847"];

    private static async Task<Document> AddArmsAsync(Document document, IReadOnlyDictionary<SwitchExpressionSyntax, List<PatternSyntax>> missing, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var newRoot = root!.ReplaceNodes(missing.Keys, (original, current) => WithArms(current, missing[original]));
        return await CodeFixSyntax.ShortenNamesAsync(document.WithSyntaxRoot(newRoot), ct).ConfigureAwait(false);
    }

    private static SyntaxToken Comma(SyntaxTriviaList trailing) =>
        Token(SyntaxTriviaList.Empty, SyntaxKind.CommaToken, trailing);

    /// <summary>
    /// The whitespace the arm's line starts with.
    /// </summary>
    private static SyntaxTriviaList IndentationOf(SwitchExpressionArmSyntax arm)
    {
        var leading = arm.GetLeadingTrivia();
        var lineStart = leading.Count - leading.Reverse().TakeWhile(t => !t.IsKind(SyntaxKind.EndOfLineTrivia) && !t.IsDirective).Count();

        return TriviaList(leading.Skip(lineStart).TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)));
    }

    /// <summary>
    /// For each switch the diagnostics are on, and the fix is offered for, patterns for what it misses:
    /// where its arms look inside the input, written the same way (<c>Kitten { Toy: Ball }</c>).
    /// </summary>
    private static async Task<Dictionary<SwitchExpressionSyntax, List<PatternSyntax>>> MissingAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken ct)
    {
        var missing = new Dictionary<SwitchExpressionSyntax, List<PatternSyntax>>();
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return missing;
        }

        var session = new Session(document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        foreach (var diagnostic in diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not SwitchExpressionSyntax { Arms.Count: > 0 } switchExpr
                || missing.ContainsKey(switchExpr)
                || switchExpr.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia))
            )
            {
                continue;
            }

            var coverage = SwitchCoverage.Of(switchExpr, model, session, ct, countGuarded: false);
            var nested = model.GetOperation(switchExpr, ct) is ISwitchExpressionOperation switchOp
                ? NestedCoverage.Missing(switchOp.Value, SwitchLabel.Of(switchOp), switchExpr, model, session, ct)
                : null;

            if (nested is { Count: > 0 })
            {
                missing[switchExpr] = nested;
            }
            else if (coverage.OverCases && coverage.Missing.Count > 0)
            {
                missing[switchExpr] = MissingPatterns.Of(coverage, model, switchExpr.SpanStart);
            }
        }

        return missing;
    }

    private static SwitchExpressionArmSyntax NewArm(PatternSyntax pattern) => SwitchExpressionArm(
        pattern,
        whenClause: null,
        Token(TriviaList(Space), SyntaxKind.EqualsGreaterThanToken, TriviaList(Space)),
        ThrowExpression(CodeFixSyntax.NewException("System.NotImplementedException")).NormalizeWhitespace()
    );

    /// <summary>
    /// The switch with arms for the cases after its last one, laid out like the existing ones:
    /// each on a line of its own, indented like the first arm and with the same line breaks, if the arms start on a line of their own, and otherwise inline.
    /// Whatever ends the last arm's line (a comment, say) stays there.
    /// </summary>
    private static SwitchExpressionSyntax WithArms(SwitchExpressionSyntax switchExpr, IReadOnlyList<PatternSyntax> cases)
    {
        var lineBreak = switchExpr.OpenBraceToken.TrailingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var multiline = lineBreak.IsKind(SyntaxKind.EndOfLineTrivia);
        var indentation = multiline ? IndentationOf(switchExpr.Arms[0]) : SyntaxTriviaList.Empty;
        var separator = multiline ? TriviaList(lineBreak) : TriviaList(Space);

        var elements = switchExpr.Arms.GetWithSeparators();
        var last = elements[elements.Count - 1];
        var tail = last.GetTrailingTrivia();
        var (kept, end) = tail.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) ? (tail, separator) : (separator, tail);

        var hadTrailingComma = last.IsToken;
        var list = elements.Replace(last, hadTrailingComma ? last.AsToken().WithTrailingTrivia(kept) : last.AsNode()!.WithoutTrailingTrivia());
        if (!hadTrailingComma)
        {
            list = list.Add(Comma(kept));
        }

        for (var i = 0; i < cases.Count; i++)
        {
            var arm = NewArm(cases[i]).WithLeadingTrivia(indentation);
            var isLast = i == cases.Count - 1;
            if (isLast && !hadTrailingComma)
            {
                list = list.Add(arm.WithTrailingTrivia(end));
            }
            else
            {
                list = list.Add(arm);
                list = list.Add(Comma(isLast ? end : separator));
            }
        }

        return switchExpr.WithArms(SeparatedList<SwitchExpressionArmSyntax>(list));
    }

    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) =>
            await AddArmsAsync(document, await MissingAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false)
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
                $"Add arms for {string.Join(", ", missing.Values.SelectMany(patterns => patterns))}",
                ct => AddArmsAsync(context.Document, missing, ct),
                equivalenceKey: nameof(AddMissingArmsCodeFix)
            ),
            context.Diagnostics
        );
    }
}

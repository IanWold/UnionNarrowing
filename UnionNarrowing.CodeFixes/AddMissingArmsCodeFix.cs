using System.Collections.Immutable;

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
        ["CS8509", "CS8846", "CS8655", "CS8847", "UN0020"];

    private static async Task<Document> AddArmsAsync(Document document, IReadOnlyDictionary<SwitchExpressionSyntax, List<PatternSyntax>> missing, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var options = await document.GetOptionsAsync(ct).ConfigureAwait(false);
        var newRoot = root!.ReplaceNodes(missing.Keys, (original, current) => original.Arms.Count == 0 ? WithFirstArms(original, current, missing[original], options) : WithArms(current, missing[original]));
        return await CodeFixSyntax.ShortenNamesAsync(document.WithSyntaxRoot(newRoot), ct).ConfigureAwait(false);
    }

    private static SyntaxToken Comma(SyntaxTriviaList trailing) =>
        Token(SyntaxTriviaList.Empty, SyntaxKind.CommaToken, trailing);

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
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not SwitchExpressionSyntax switchExpr
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
    /// The switch with arms for the cases after its last one, laid out like the existing ones (an empty switch gets <see cref="WithFirstArms"/> instead):
    /// each on a line of its own, indented like the first arm and with the same line breaks, if the arms start on a line of their own, and otherwise inline.
    /// Whatever ends the last arm's line (a comment, say) stays there.
    /// </summary>
    private static SwitchExpressionSyntax WithArms(SwitchExpressionSyntax switchExpr, IReadOnlyList<PatternSyntax> cases)
    {
        var lineBreak = switchExpr.OpenBraceToken.TrailingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var multiline = lineBreak.IsKind(SyntaxKind.EndOfLineTrivia);
        var indentation = multiline ? CodeFixSyntax.Indentation(switchExpr.Arms[0].GetLeadingTrivia()) : SyntaxTriviaList.Empty;
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

    /// <summary>
    /// An empty switch (<paramref name="current"/> is <paramref name="original"/> as it is so far) with arms for the cases, one per line, each with a comma after it, and the <c>}</c> on a line of its own:
    /// indented one level past the line the <c>{</c> is on, as the IDE's formatting would.
    /// A level is a tab where that line is indented with tabs, and otherwise what the document's options say, and the line breaks are that line's.
    /// </summary>
    private static SwitchExpressionSyntax WithFirstArms(SwitchExpressionSyntax original, SwitchExpressionSyntax current, IReadOnlyList<PatternSyntax> cases, DocumentOptionSet options)
    {
        var text = original.SyntaxTree.GetText();
        var line = text.Lines.GetLineFromPosition(original.OpenBraceToken.SpanStart);
        var lineIndentation = new string(line.ToString().TakeWhile(c => c is ' ' or '\t').ToArray());
        var level = lineIndentation.IndexOf('\t') >= 0 || options.GetOption(FormattingOptions.UseTabs) ? "\t" : new string(' ', options.GetOption(FormattingOptions.IndentationSize));
        var lineBreak = EndOfLine(line.EndIncludingLineBreak > line.End ? text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak)) : options.GetOption(FormattingOptions.NewLine));
        var open = current.OpenBraceToken;
        var close = current.CloseBraceToken;
        var onOneLine = !open.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var arms = cases.Select(pattern => NewArm(pattern).WithLeadingTrivia(Whitespace(lineIndentation + level)));
        var withArms = current.WithArms(SeparatedList(arms, cases.Select(_ => Comma(TriviaList(lineBreak)))));

        return onOneLine
            ? withArms
                .WithOpenBraceToken(open.WithTrailingTrivia(TriviaList(open.TrailingTrivia.Reverse().SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse()).Add(lineBreak)))
                .WithCloseBraceToken(close.WithLeadingTrivia(close.LeadingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia)).Prepend(Whitespace(lineIndentation))))
            : withArms;
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

using System.Collections.Immutable;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For a throwing default that catches cases (UN0002):
/// names those cases instead.
/// In a switch expression, <c>_ =&gt; throw ...</c> becomes <c>Dog or Bird =&gt; throw ...</c>, so the compiler reports a case added later as missing.
/// In a switch statement, the default section gets <c>case</c> labels for them, and keeps its <c>default:</c> (which definite return still needs), where this analyzer will report a case added later.
/// For a throw after a switch statement, a section for them is added that throws the same way;
/// for a throw after a chain of ifs, one more if, for them, before it.
/// Where the arms or tests look inside the cases, what's caught is named the same way (<c>Kitten { Toy: Mouse }</c>).
/// For a switch over a tuple, what's named is the combinations, as Add missing arms writes them, unless that's all of them (<c>(_, _)</c>).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ListCaughtCasesCodeFix)), Shared]
public sealed class ListCaughtCasesCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.ThrowingDefault.Id];

    /// <summary>
    /// For a switch over a tuple, the combinations its default catches, as Add missing arms writes them:
    /// where the arms look inside the cases, what they miss inside them (<c>(Kitten { Toy: Mouse }, _)</c>).
    /// None where one of them matches anything (<c>(_, _)</c>), since naming that wouldn't change what the default catches.
    /// </summary>
    private static List<PatternSyntax> CaughtCombinations(SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var switchOperation = model.GetOperation(switchNode, ct);
        var labels = SwitchLabel.Of(switchOperation);
        List<SwitchLabel> beforeDefault = switchOperation is ISwitchExpressionOperation ? [.. labels.Take(labels.Count - 1)] : [.. labels.Where(label => !label.IsDefault)];

        var caught = switchOperation switch { ISwitchExpressionOperation expression => expression.Value, ISwitchOperation statement => statement.Value, _ => null } is { } input
            && NestedCoverage.Missing(input, beforeDefault, switchNode, model, session, ct) is { Count: > 0 } inside
                ? inside
                : SwitchCoverage.Of(switchNode, model, session, ct, countGuarded: false, withoutDefault: true) is { OverCases: true, Missing.Count: > 0 } coverage
                    ? MissingPatterns.Of(coverage, model, switchNode.SpanStart)
                    : [];

        return caught.Any(MatchesAnything) ? [] : caught;
    }

    private static PatternSyntax Either(IReadOnlyList<PatternSyntax> patterns) =>
        patterns.Skip(1).Aggregate(patterns[0], (left, right) => BinaryPattern(SyntaxKind.OrPattern, left, right));

    /// <summary>
    /// Whether a pattern matches anything: <c>_</c>, or <c>(_, _)</c>.
    /// </summary>
    private static bool MatchesAnything(PatternSyntax pattern) =>
        pattern is DiscardPatternSyntax
        || pattern is RecursivePatternSyntax { Type: null, PropertyPatternClause: null, PositionalPatternClause: { } positional } && positional.Subpatterns.All(sub => MatchesAnything(sub.Pattern));

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
        var newElseKeyword = elseKeyword.WithLeadingTrivia(CodeFixSyntax.Indentation(elseKeyword.LeadingTrivia)).WithTrailingTrivia(Space);
        var newRoot = root!.ReplaceNode(elseClause, elseClause.WithElseKeyword(newElseKeyword).WithStatement(test));
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
            var indentation = CodeFixSyntax.Indentation(throwStatement.GetLeadingTrivia());
            newRoot = root!.InsertNodesBefore(throwStatement, [test.WithLeadingTrivia(indentation).WithTrailingTrivia(lineBreak)]);
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
            case SwitchExpressionSyntax { Arms.Count: > 1, GoverningExpression: TupleExpressionSyntax } tupleSwitch
                when CaughtCombinations(tupleSwitch, model, session, ct) is { Count: > 0 } combinations:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(combinations)} in the throwing arm", ct => NameInArmAsync(context.Document, tupleSwitch, combinations, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchExpressionSyntax { Arms.Count: > 1 } switchExpr when Handling.BeforeDefaultOf(switchExpr, model, session, ct) is { } handling
                && handling.CaughtPatterns(model, ct) is { Count: > 0 } caught:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caught)} in the throwing arm", ct => NameInArmAsync(context.Document, switchExpr, caught, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case ThrowStatementSyntax throwStatement when Handling.BeforeThrow(throwStatement, model, session, ct) is { } after
                && after.CaughtPatterns(model, ct) is { Count: > 0 } caughtAfter:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caughtAfter)} before the throw", ct => NameBeforeThrowAsync(context.Document, throwStatement, after.Start, after.Input, caughtAfter, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            // The else that ends a chain of ifs (UN0002 is on its keyword).
            case ElseClauseSyntax elseClause when TopOfChain(elseClause) is { } top
                && model.GetOperation(top, ct) is IConditionalOperation topOp
                && Handling.BeforeThrowingElse(topOp, model, session, ct) is { } chain && chain.Else == elseClause
                && chain.Handling.CaughtPatterns(model, ct) is { Count: > 0 } caughtByElse:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caughtByElse)} before the else", ct => NameBeforeElseAsync(context.Document, elseClause, chain.Handling.Input, caughtByElse, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchStatementSyntax { Expression: TupleExpressionSyntax } tupleStatement
                when CaughtCombinations(tupleStatement, model, session, ct) is { Count: > 0 } combinations:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(combinations)} in the default section", ct => NameInSectionAsync(context.Document, tupleStatement, combinations, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchStatementSyntax switchStatement when Handling.BeforeDefaultOf(switchStatement, model, session, ct) is { } handling
                && handling.CaughtPatterns(model, ct) is { Count: > 0 } caught:
                context.RegisterCodeFix(
                    CodeAction.Create($"Handle {Names(caught)} in the default section", ct => NameInSectionAsync(context.Document, switchStatement, caught, ct), equivalenceKey: nameof(ListCaughtCasesCodeFix)),
                    context.Diagnostics
                );
                break;
        }
    }
}

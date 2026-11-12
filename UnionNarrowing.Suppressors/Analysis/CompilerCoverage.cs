namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// The compiler's own judgement of whether a switch expression is exhaustive once what earlier code rules out is out of the way:
/// it reasons about patterns the analysis doesn't (a case split by list patterns, relational patterns, strings, or members that aren't unions), so where the analysis can't tell, it asks.
/// </summary>
internal static class CompilerCoverage
{
    /// <summary>
    /// What the compiler reports for a switch expression that misses values of its input (or null, or an enum's unnamed values).
    /// </summary>
    private static readonly HashSet<string> NotExhaustive = ["CS8509", "CS8524", "CS8846", "CS8655", "CS8847"];

    /// <summary>
    /// What the compiler reports for an arm that only matches what earlier arms already do, which the arms added here can be.
    /// </summary>
    private const string AlreadyHandled = "CS8510";

    /// <summary>
    /// Whether the compiler finds the switch expression exhaustive with an arm added for each of <paramref name="ruledOut"/> (<c>Case =&gt; throw null!</c>), which only match what earlier code rules out.
    /// With <paramref name="withoutDefault"/>, its last arm is left out first.
    /// False if the switch with them added has any other error (a name the patterns use that doesn't bind there, say), since the compiler may not check it then.
    /// </summary>
    public static bool CompleteWith(SwitchExpressionSyntax switchExpr, bool withoutDefault, IReadOnlyList<PatternSyntax> ruledOut, SemanticModel model, CancellationToken ct)
    {
        if (ruledOut.Count == 0 || (withoutDefault && switchExpr.Arms.Count == 0) || switchExpr.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            return false;
        }

        var kept = withoutDefault ? switchExpr.Arms.Take(switchExpr.Arms.Count - 1) : switchExpr.Arms;
        var added = ruledOut.Select(pattern => SwitchExpressionArm(pattern, ThrowExpression(PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, LiteralExpression(SyntaxKind.NullLiteralExpression)))));
        var annotation = new SyntaxAnnotation();
        var augmented = switchExpr.WithArms(SeparatedList(kept.Concat(added))).WithAdditionalAnnotations(annotation);
        var tree = switchExpr.SyntaxTree;
        var forkedTree = tree.WithRootAndOptions(tree.GetRoot(ct).ReplaceNode(switchExpr, augmented), tree.Options);
        var forkedSwitch = forkedTree.GetRoot(ct).GetAnnotatedNodes(annotation).OfType<SwitchExpressionSyntax>().FirstOrDefault();

        if (forkedSwitch is null)
        {
            return false;
        }

        var diagnostics = model.Compilation.ReplaceSyntaxTree(tree, forkedTree).GetSemanticModel(forkedTree).GetDiagnostics(forkedSwitch.Span, ct);

        return !diagnostics.Any(d =>
            NotExhaustive.Contains(d.Id) && d.Location.SourceSpan.Start == forkedSwitch.SwitchKeyword.SpanStart
            || d.Severity == DiagnosticSeverity.Error && d.Id != AlreadyHandled
        );
    }
}

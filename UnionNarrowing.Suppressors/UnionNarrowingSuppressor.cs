using System.Collections.Immutable;

namespace UnionNarrowing.Suppressors;

/// <summary>
/// Suppresses CS8509 on a switch expression over a union (or a closed hierarchy) when every case the switch doesn't handle was already ruled out by earlier code:
/// an <c>if (x is T) &lt;exit&gt;</c> guard on the same variable, the branch of a type test the switch sits in, an earlier arm of an enclosing switch, and so on.
/// Likewise CS8846 (the same, with <c>when</c> arms), and CS8655 and CS8847 (null not handled), when earlier code ruled out null.
/// It only suppresses what it can prove.
/// Anything it doesn't understand leaves the warning in place, so adding a new case type to the union still lights up every switch that misses it.
/// Where earlier code rules out only some of the cases a switch doesn't handle, UN0020 reports the ones that are left, and CS8509 (or CS8846), whose example could name one that was ruled out, is suppressed, unless UN0020 is turned off.
/// It also suppresses the IDE's "populate switch" suggestions:
/// IDE0072 on any switch expression over a union or closed hierarchy (CS8509 already reports what's missing), and IDE0010 on a switch statement that handles every case that's left.
/// The IDE doesn't understand unions, and its fix adds a throwing default, which would hide new cases.
/// </summary>
/// <remarks>
/// Like the compiler's own exhaustiveness check, this trusts the union type to be well behaved:
/// a union's <c>Value</c> only changes when the union itself is written.
/// That holds for <c>union</c> declarations, and for custom struct unions unless their <c>Value</c> reads through to some other, shared state.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnionNarrowingSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor NotExhaustive = new(
        id: "UN0001",
        suppressedDiagnosticId: "CS8509",
        justification: "The case types this switch doesn't handle were excluded by earlier type checks on the same variable."
    );

    private static readonly SuppressionDescriptor NotExhaustiveDespiteWhen = new(
        id: "UN0011",
        suppressedDiagnosticId: "CS8846",
        justification: "The cases this switch doesn't handle were excluded by earlier type checks on the same variable, or are handled by arms whose `when` clauses always hold for them."
    );

    private static readonly SuppressionDescriptor NotExhaustiveReportedInstead = new(
        id: "UN0021",
        suppressedDiagnosticId: "CS8509",
        justification: "Earlier code rules out some of the cases this switch doesn't handle, which the compiler's example could name. "
            + "UN0020 reports the warning instead, with the cases earlier code leaves."
    );

    private static readonly SuppressionDescriptor NotExhaustiveDespiteWhenReportedInstead = new(
        id: "UN0022",
        suppressedDiagnosticId: "CS8846",
        justification: "Earlier code rules out some of the cases this switch doesn't handle, which the compiler's example could name. "
            + "UN0020 reports the warning instead, with the cases earlier code leaves."
    );

    private static readonly SuppressionDescriptor NullNotHandled = new(
        id: "UN0012",
        suppressedDiagnosticId: "CS8655",
        justification: "Earlier code on the same variable already ruled out null, which is the only input the switch doesn't handle."
    );

    private static readonly SuppressionDescriptor NullNotHandledDespiteWhen = new(
        id: "UN0013",
        suppressedDiagnosticId: "CS8847",
        justification: "Earlier code on the same variable already ruled out null, which is the only input the switch doesn't handle apart from what `when` arms might match."
    );

    private static readonly SuppressionDescriptor TupleCombinationsHandled = new(
        id: "UN0023",
        suppressedDiagnosticId: "CS8509",
        justification: "The tuple switch handles every combination of its elements' cases; the compiler misses some that arms with wildcards in different places cover together."
    );

    private static readonly SuppressionDescriptor PopulateSwitchExpression = new(
        id: "UN0008",
        suppressedDiagnosticId: "IDE0072",
        justification: "The switch handles every case of the union that earlier code doesn't rule out; a default arm would hide cases added later."
    );

    private static readonly SuppressionDescriptor PopulateSwitchStatement = new(
        id: "UN0009",
        suppressedDiagnosticId: "IDE0010",
        justification: "The switch handles every case of the union that earlier code doesn't rule out; a default section would hide cases added later."
    );

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions =>
        ImmutableArray.Create(
            NotExhaustive, NotExhaustiveDespiteWhen, NotExhaustiveReportedInstead, NotExhaustiveDespiteWhenReportedInstead, NullNotHandled, NullNotHandledDespiteWhen,
            TupleCombinationsHandled, PopulateSwitchExpression, PopulateSwitchStatement
        );

    /// <summary>
    /// The suppression for a compiler warning that a switch expression misses cases:
    /// CS8509;
    /// CS8846, which is CS8509 when an arm with a <c>when</c> clause might cover what's missing;
    /// and CS8655 and CS8847, the same two for null alone, where earlier code (a Where filter, the callers of a helper, a copy) rules null out in ways the compiler's flow state doesn't follow.
    /// </summary>
    private static SuppressionDescriptor? ForMissingCases(string id) =>
        id switch
        {
            "CS8509" => NotExhaustive,
            "CS8846" => NotExhaustiveDespiteWhen,
            "CS8655" => NullNotHandled,
            "CS8847" => NullNotHandledDespiteWhen,
            _ => null,
        };

    /// <summary>
    /// The suppression for a compiler warning that a switch expression misses cases (CS8509, CS8846), when UN0020 reports what it misses instead (see <see cref="MissingCasesAnalyzer"/>).
    /// </summary>
    private static SuppressionDescriptor? ForCasesReportedInstead(string id) => id switch
    {
        "CS8509" => NotExhaustiveReportedInstead,
        "CS8846" => NotExhaustiveDespiteWhenReportedInstead,
        _ => null,
    };

    /// <summary>
    /// Whether UN0020 can report what a CS8509 (or CS8846) at <paramref name="position"/> would: it's reported there at least as severely as the compiler warning (a warning at the least), and nothing hides it there (a <c>#pragma warning disable</c>, or <c>[SuppressMessage]</c>).
    /// </summary>
    private static bool HandsOffTo(DiagnosticDescriptor descriptor, string compilerId, int position, SemanticModel model, AnalyzerConfigOptionsProvider config, CancellationToken ct)
    {
        var tree = model.SyntaxTree;
        var reported = Rank(Severity(descriptor.Id, descriptor.DefaultSeverity, descriptor.Category, tree, model.Compilation, config, ct));

        return reported >= Rank(Severity(compilerId, DiagnosticSeverity.Warning, category: null, tree, model.Compilation, config, ct))
            && reported >= Rank(ReportDiagnostic.Warn)
            && !PragmaDisables(descriptor.Id, tree, position, ct)
            && !SuppressMessageFor(descriptor.Id, model.GetEnclosingSymbol(position, ct), model.Compilation);
    }

    /// <summary>
    /// The severity a diagnostic is reported at in <paramref name="tree"/>:
    /// as set by its ID (for the compilation, as NoWarn, WarningsAsErrors or a ruleset do, then in .editorconfig or a .globalconfig), or else, for an analyzer's, by its category or for every analyzer; a warning becomes an error with TreatWarningsAsErrors, unless WarningsNotAsErrors names it
    /// (which sets it to <see cref="ReportDiagnostic.Default"/> for the compilation, leaving the rest to decide).
    /// </summary>
    private static ReportDiagnostic Severity(string id, DiagnosticSeverity byDefault, string? category, SyntaxTree tree, Compilation compilation, AnalyzerConfigOptionsProvider config, CancellationToken ct)
    {
        var treeOptions = compilation.Options.SyntaxTreeOptionsProvider;
        var notAsError = compilation.Options.SpecificDiagnosticOptions.TryGetValue(id, out var report) && report == ReportDiagnostic.Default;
        if (report == ReportDiagnostic.Default
            && !(treeOptions is not null && (treeOptions.TryGetDiagnosticValue(tree, id, ct, out report) || treeOptions.TryGetGlobalDiagnosticValue(id, ct, out report)))
            && category is not null)
        {
            var bulk = new[] { $"dotnet_analyzer_diagnostic.category-{category}.severity", "dotnet_analyzer_diagnostic.severity" }
                .Select(key => config.GetOptions(tree).TryGetValue(key, out var severity) || config.GlobalOptions.TryGetValue(key, out severity) ? severity : null)
                .FirstOrDefault(severity => severity is not null);

            report = bulk?.Trim().ToLowerInvariant() switch
            {
                "none" => ReportDiagnostic.Suppress,
                "silent" => ReportDiagnostic.Hidden,
                "suggestion" => ReportDiagnostic.Info,
                "warning" => ReportDiagnostic.Warn,
                "error" => ReportDiagnostic.Error,
                _ => ReportDiagnostic.Default,
            };
        }

        if (report == ReportDiagnostic.Default)
        {
            report = byDefault switch
            {
                DiagnosticSeverity.Error => ReportDiagnostic.Error,
                DiagnosticSeverity.Warning => ReportDiagnostic.Warn,
                DiagnosticSeverity.Info => ReportDiagnostic.Info,
                _ => ReportDiagnostic.Hidden,
            };
        }

        return report == ReportDiagnostic.Warn && compilation.Options.GeneralDiagnosticOption == ReportDiagnostic.Error && !notAsError ? ReportDiagnostic.Error : report;
    }

    /// <summary>
    /// How severe a report is, from not at all to an error (<see cref="ReportDiagnostic"/>'s own order isn't by severity).
    /// </summary>
    private static int Rank(ReportDiagnostic report) => report switch
    {
        ReportDiagnostic.Error => 4,
        ReportDiagnostic.Warn => 3,
        ReportDiagnostic.Info => 2,
        ReportDiagnostic.Hidden => 1,
        _ => 0,
    };

    /// <summary>
    /// Whether a <c>#pragma warning disable</c> (of the ID, or of everything) is in force at <paramref name="position"/>.
    /// </summary>
    private static bool PragmaDisables(string id, SyntaxTree tree, int position, CancellationToken ct)
    {
        var disabled = false;
        foreach (var pragma in tree.GetRoot(ct).DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().TakeWhile(p => p.SpanStart < position))
        {
            if (pragma.ErrorCodes.Count == 0 || pragma.ErrorCodes.Any(code => code is IdentifierNameSyntax { Identifier.ValueText: var name } && name == id))
            {
                disabled = pragma.DisableOrRestoreKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.DisableKeyword);
            }
        }

        return disabled;
    }

    /// <summary>
    /// Whether a <c>[SuppressMessage]</c> (or <c>[UnconditionalSuppressMessage]</c>) for the ID is on the symbol, the property, indexer or event an accessor is of, a member or type it's in, or the assembly.
    /// </summary>
    private static bool SuppressMessageFor(string id, ISymbol? symbol, Compilation compilation)
    {
        static bool Suppresses(AttributeData attribute, string id) =>
            attribute.AttributeClass?.Name is "SuppressMessageAttribute" or "UnconditionalSuppressMessageAttribute"
            && attribute.ConstructorArguments.Length == 2
            && attribute.ConstructorArguments[1].Value is string checkId
            && checkId.StartsWith(id, StringComparison.OrdinalIgnoreCase);

        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current.GetAttributes().Any(attribute => Suppresses(attribute, id))
                || (current is IMethodSymbol { AssociatedSymbol: { } associated } && associated.GetAttributes().Any(attribute => Suppresses(attribute, id))))
            {
                return true;
            }
        }

        return compilation.Assembly.GetAttributes().Concat(compilation.SourceModule.GetAttributes()).Any(attribute => Suppresses(attribute, id));
    }

    /// <summary>
    /// The suppression for a diagnostic of this kind reported at <paramref name="token"/>, if it applies.
    /// </summary>
    private static SuppressionDescriptor? SuppressionFor(string id, SyntaxToken token, SemanticModel model, Session session, AnalyzerConfigOptionsProvider config, CancellationToken ct)
    {
        // The compiler reports these on the `switch` keyword of the switch expression.
        if (ForMissingCases(id) is { } descriptor)
        {
            if (token.Parent is not SwitchExpressionSyntax switchExpr)
            {
                return null;
            }

            // A tuple switch the compiler misjudges: its arms, with no `when` clauses, handle every combination by
            // themselves, but the compiler's decision tree loses track where wildcards sit in different places.
            var coverage = SwitchCoverage.Of(switchExpr, model, session, ct);

            return coverage is { Complete: true, ThanksToEarlierCode: true }
                ? descriptor
                : coverage is { Complete: true } && id == "CS8509" && WriteAnalysis.WithoutParentheses(switchExpr.GoverningExpression) is TupleExpressionSyntax
                    && switchExpr.Arms.All(arm => arm.WhenClause is null)
                    ? TupleCombinationsHandled
                    : model.GetOperation(switchExpr, ct) is ISwitchExpressionOperation switchOp && MissingCasesAnalyzer.CasesLeft(coverage, switchOp, model, session, ct) is { Count: > 0 }
                        && HandsOffTo(Descriptors.MissingCases, id, token.SpanStart, model, config, ct)
                        ? ForCasesReportedInstead(id)
                        : null;
        }

        return id switch
        {
            // On a switch expression that misses a case, CS8509 already says so, and Add missing arms is the fix to
            // offer; the IDE's would add the throwing default that hides cases.
            "IDE0072" when token.Parent?.AncestorsAndSelf().OfType<SwitchExpressionSyntax>().FirstOrDefault() is { } switchExpr
                && SwitchCoverage.Of(switchExpr, model, session, ct) is { OverCases: true } => PopulateSwitchExpression,

            "IDE0010" when token.Parent?.AncestorsAndSelf().OfType<SwitchStatementSyntax>().FirstOrDefault() is { } switchStatement
                && SwitchCoverage.Of(switchStatement, model, session, ct) is { OverCases: true, Complete: true } => PopulateSwitchStatement,

            _ => null,
        };
    }

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var ct = context.CancellationToken;
        var config = context.Options.AnalyzerConfigOptionsProvider;
        var session = new Session(config);

        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree)
            {
                continue;
            }

            var token = tree.GetRoot(ct).FindToken(diagnostic.Location.SourceSpan.Start);

            if (SuppressionFor(diagnostic.Id, token, context.GetSemanticModel(tree), session, config, ct) is { } descriptor)
            {
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
            }
        }
    }
}

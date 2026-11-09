using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors;

/// <summary>
/// Suppresses CS8509 on a switch expression over a union (or a closed hierarchy) when every case the switch doesn't handle was already ruled out by earlier code:
/// an <c>if (x is T) &lt;exit&gt;</c> guard on the same variable, the branch of a type test the switch sits in, an earlier arm of an enclosing switch, and so on.
/// Likewise CS8846 (the same, with <c>when</c> arms), and CS8655 and CS8847 (null not handled), when earlier code ruled out null.
/// It only suppresses what it can prove.
/// Anything it doesn't understand leaves the warning in place, so adding a new case type to the union still lights up every switch that misses it.
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
        ImmutableArray.Create(NotExhaustive, NotExhaustiveDespiteWhen, NullNotHandled, NullNotHandledDespiteWhen, PopulateSwitchExpression, PopulateSwitchStatement);

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
    /// The suppression for a diagnostic of this kind reported at <paramref name="token"/>, if it applies.
    /// </summary>
    private static SuppressionDescriptor? SuppressionFor(string id, SyntaxToken token, SemanticModel model, Session session, CancellationToken ct)
    {
        // The compiler reports these on the `switch` keyword of the switch expression.
        if (ForMissingCases(id) is { } descriptor)
        {
            return token.Parent is SwitchExpressionSyntax switchExpr && SwitchCoverage.Of(switchExpr, model, session, ct) is { Complete: true, ThanksToEarlierCode: true }
                ? descriptor
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
        var session = new Session(context.Options.AnalyzerConfigOptionsProvider);
        
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree)
            {
                continue;
            }

            var token = tree.GetRoot(ct).FindToken(diagnostic.Location.SourceSpan.Start);

            if (SuppressionFor(diagnostic.Id, token, context.GetSemanticModel(tree), session, ct) is { } descriptor)
            {
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
            }
        }
    }
}

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports a switch expression the compiler warns isn't exhaustive (CS8509, or CS8846 with <c>when</c> arms) when earlier code rules out some of the cases its arms don't handle, but not all of them (UN0020):
/// the compiler's example of a case that isn't handled can then be one that never gets there.
/// This names the cases that do, and the suppressor takes the compiler's warning away (see <see cref="UnionNarrowingSuppressor"/>).
/// </summary>
/// <remarks>Reported in generated code too, like the compiler's warning it stands in for.</remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingCasesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.MissingCases];

    private static void AnalyzeSwitchExpression(OperationAnalysisContext context, Session session)
    {
        var switchOp = (ISwitchExpressionOperation)context.Operation;
        var switchExpr = (SwitchExpressionSyntax)switchOp.Syntax;
        var ct = context.CancellationToken;

        if (switchOp.SemanticModel is not { } model
            || CasesLeft(SwitchCoverage.Of(switchExpr, model, session, ct), switchOp, model, session, ct) is not { Count: > 0 } left
            || !model.GetDiagnostics(switchExpr.SwitchKeyword.Span, ct).Any(d => d.Id is "CS8509" or "CS8846" && !d.IsSuppressed)
        )
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.MissingCases, switchExpr.SwitchKeyword.GetLocation(), string.Join(", ", left)));
    }

    /// <summary>
    /// What UN0020 names for a switch expression with this coverage, where it stands in for the compiler's warning:
    /// the cases (or, for arms that look inside, the combinations) the arms miss, where earlier code rules out others they don't handle, which the compiler's example could name.
    /// Null where it doesn't stand in.
    /// </summary>
    internal static IReadOnlyList<PatternSyntax>? CasesLeft(SwitchCoverage coverage, ISwitchExpressionOperation switchOp, SemanticModel model, Session session, CancellationToken ct)
    {
        if (coverage.Complete || switchOp.Syntax is not SwitchExpressionSyntax switchExpr)
        {
            return null;
        }

        var (inside, ruledOutInside) = NestedCoverage.Left(switchOp.Value, SwitchLabel.Of(switchOp), switchExpr, model, session, ct);
        if (coverage is { ThanksToEarlierCode: true, Missing.Count: > 0 })
        {
            return inside is { Count: > 0 } ? inside : MissingPatterns.Of(coverage, model, switchExpr.SpanStart);
        }

        return ruledOutInside && inside is { Count: > 0 } ? inside : null;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var session = new Session(start.Options.AnalyzerConfigOptionsProvider);
            start.RegisterOperationAction(c => AnalyzeSwitchExpression(c, session), OperationKind.SwitchExpression);
        });
    }
}

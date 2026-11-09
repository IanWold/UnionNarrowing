using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports a switch over a union or closed hierarchy whose default only throws, when some case reaches that default.
/// A throwing default says "this can't happen", which turns off the compiler's check that every case is handled:
/// the switch compiles even when a case is missing, including one added to the type later.
/// </summary>
/// <remarks>Switch statements need a default like this to satisfy definite return (CS0161) even when they handle every case, so this is the only exhaustiveness check they get.</remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThrowingDefaultAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.ThrowingDefault, Descriptors.ThrowingDefaultOnCompleteSwitch];

    /// <summary>
    /// An <c>if</c>/<c>else if</c> chain of tests on one union or closed hierarchy value, at least two long, ending in an <c>else</c> that only throws:
    /// the cases no test is sure to catch end up there.
    /// </summary>
    private static void AnalyzeIfChain(OperationAnalysisContext context, Session session)
    {
        var top = (IConditionalOperation)context.Operation;
        if (top.Syntax is IfStatementSyntax { Parent: not ElseClauseSyntax } && top.SemanticModel is { } model && Handling.BeforeThrowingElse(top, model, session, context.CancellationToken) is var (elseClause, handling))
        {
            Report(context, handling, elseClause.ElseKeyword.GetLocation(), "else", "chain of ifs", out _);
        }
    }

    private static void AnalyzeSwitchExpression(OperationAnalysisContext context, Session session)
    {
        var switchOp = (ISwitchExpressionOperation)context.Operation;
        if (switchOp.Arms.LastOrDefault() is not { Guard: null } last
            || !(last.Pattern is IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }
                || switchOp.Value is ITupleOperation && last.Pattern is IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } all && all.DeconstructionSubpatterns.All(Facts.MatchesAnything)
            )
            || !IsThrowExpression(last.Value)
            || switchOp.SemanticModel is not { } model
        )
        {
            return;
        }

        var location = ((SwitchExpressionSyntax)switchOp.Syntax).SwitchKeyword.GetLocation();
        if (switchOp.Value is ITupleOperation)
        {
            ReportTuple(context, location, "discard arm", last.Syntax.GetLocation(), session);
            return;
        }

        if (Handling.BeforeDefaultOf(switchOp.Syntax, model, session, context.CancellationToken) is not { } handling || Report(context, handling, location, "discard arm", "switch", out var excluded))
        {
            return;
        }

        // Every case is handled. Unless null gets to the arm (and the compiler would want it handled), all it does is
        // catch cases added later.
        if (!handling.Space.NullMatters || handling.Handled.IncludesNull || excluded.IncludesNull)
        {
            var name = switchOp.Value.Type?.ToMinimalDisplayString(model, switchOp.Syntax.SpanStart) ?? "the type";
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThrowingDefaultOnCompleteSwitch, last.Syntax.GetLocation(), "discard arm", name));
        }
    }

    private static void AnalyzeSwitchStatement(OperationAnalysisContext context, Session session)
    {
        var switchOp = (ISwitchOperation)context.Operation;
        if (switchOp.Cases.FirstOrDefault(c => c.Clauses.Any(clause => clause is IDefaultCaseClauseOperation)) is not { } defaultCase || !Handling.OnlyThrows(defaultCase.Body) || switchOp.SemanticModel is not { } model)
        {
            return;
        }

        var location = ((SwitchStatementSyntax)switchOp.Syntax).SwitchKeyword.GetLocation();

        if (switchOp.Value is ITupleOperation)
        {
            ReportTuple(context, location, "default section", lastArm: null, session);
        }
        else if (Handling.BeforeDefaultOf(switchOp.Syntax, model, session, context.CancellationToken) is { } handling)
        {
            Report(context, handling, location, "default section", "switch", out _);
        }
    }

    /// <summary>
    /// A <c>throw</c> statement right after a switch statement over a union or closed hierarchy (with no default, and no section that breaks out of it), or after two or more <c>if</c>s that each test the value and exit:
    /// it's a default in all but name, and catches the cases they don't handle.
    /// </summary>
    private static void AnalyzeThrowAfter(OperationAnalysisContext context, Session session)
    {
        if (context.Operation.Syntax is not ThrowStatementSyntax throwStatement || context.Operation.SemanticModel is not { } model || Handling.BeforeThrow(throwStatement, model, session, context.CancellationToken) is not { } handling)
        {
            return;
        }

        var (what, container) = handling.Start is SwitchStatementSyntax
            ? ("statement after the switch", "switch")
            : ("statement after the ifs", "chain of ifs");

        Report(context, handling, throwStatement.ThrowKeyword.GetLocation(), what, container, out _);
    }

    /// <summary>
    /// Whether the arm's value is a <c>throw</c> expression, through any conversions.
    /// Unlike <see cref="Facts.IsThrow"/>, not a call to a method that never returns.
    /// </summary>
    private static bool IsThrowExpression(IOperation operation) =>
        operation switch
        {
            IThrowOperation => true,
            IConversionOperation { Operand: var operand } => IsThrowExpression(operand),
            _ => false,
        };

    /// <summary>
    /// Reports the cases that reach the default, if any.
    /// Returns whether it did, with what earlier code rules out.
    /// </summary>
    private static bool Report(OperationAnalysisContext context, Handling handling, Location location, string what, string container, out CaseSet excluded)
    {
        var model = context.Operation.SemanticModel!;

        excluded = handling.Excluded(model, context.CancellationToken);

        var caught = handling.Caught(excluded);
        if (caught.Count == 0)
        {
            return false;
        }

        var names = MissingPatterns.Names(caught, handling.Space, model, context.Operation.Syntax.SpanStart);
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThrowingDefault, location, what, names, container));
        return true;
    }

    /// <summary>
    /// For a switch over a tuple:
    /// the combinations its throwing default catches (UN0002), or, for a switch expression's arm that catches none, UN0016.
    /// </summary>
    private static void ReportTuple(OperationAnalysisContext context, Location location, string what, Location? lastArm, Session session)
    {
        var model = context.Operation.SemanticModel!;
        var switchNode = context.Operation.Syntax;
        var coverage = SwitchCoverage.Of(switchNode, model, session, context.CancellationToken, countGuarded: false, withoutDefault: true);
        
        if (!coverage.OverCases)
        {
            return;
        }

        if (coverage.Missing.Count > 0)
        {
            var names = string.Join(", ", MissingPatterns.Of(coverage, model, switchNode.SpanStart).Select(p => p.ToString()));
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThrowingDefault, location, what, names, "switch"));
        }
        else if (coverage.Complete && lastArm is not null && ((ISwitchExpressionOperation)context.Operation).Value is ITupleOperation tuple)
        {
            var unions = string.Join(", ", tuple.Elements.Select(e => e.Type).Where(t => t is { IsUnion: true } || t?.IsClosed == true)
                .Select(t => t!.ToMinimalDisplayString(model, switchNode.SpanStart)).Distinct()
            );
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThrowingDefaultOnCompleteSwitch, lastArm, what, unions));
        }
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var session = new Session(start.Options.AnalyzerConfigOptionsProvider);
            start.RegisterOperationAction(c => AnalyzeSwitchExpression(c, session), OperationKind.SwitchExpression);
            start.RegisterOperationAction(c => AnalyzeSwitchStatement(c, session), OperationKind.Switch);
            start.RegisterOperationAction(c => AnalyzeIfChain(c, session), OperationKind.Conditional);
            start.RegisterOperationAction(c => AnalyzeThrowAfter(c, session), OperationKind.Throw);
        });
    }
}

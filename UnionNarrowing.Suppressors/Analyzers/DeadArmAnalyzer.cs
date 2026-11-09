using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// The combinations of cases of a tuple literal's elements (null included, for every element), what earlier code rules out, and which combinations a pattern on the tuple could, or is sure to, match.
/// </summary>
file class TupleModel
{
    private readonly Compilation _compilation;
    private readonly List<CaseSet> _excluded;
    private readonly List<CaseSpace> _spaces;
    private readonly IReadOnlyList<CaseSet[]> _together;

    public List<object?[]> All { get; }

    private TupleModel(List<CaseSpace> spaces, List<CaseSet> excluded, IReadOnlyList<CaseSet[]> together, List<object?[]> all, Compilation compilation)
    {
        _spaces = spaces;
        _excluded = excluded;
        _together = together;
        All = all;
        _compilation = compilation;
    }

    public bool AlwaysMatches(IPatternOperation pattern, object?[] combination) => pattern switch
    {
        IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => true,
        IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == _spaces.Count =>
            recursive.DeconstructionSubpatterns.Select((sub, i) => Facts.Of(sub, _spaces[i], _compilation).AlwaysTrue.ContainsCase(combination[i])).All(x => x),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => AlwaysMatches(or.LeftPattern, combination) || AlwaysMatches(or.RightPattern, combination),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => AlwaysMatches(and.LeftPattern, combination) && AlwaysMatches(and.RightPattern, combination),
        INegatedPatternOperation not => !CouldMatch(not.Pattern, combination),
        _ => false,
    };

    public bool CouldMatch(IPatternOperation pattern, object?[] combination) => pattern switch
    {
        IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => true,
        IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == _spaces.Count =>
            recursive.DeconstructionSubpatterns.Select((sub, i) => !Facts.Of(sub, _spaces[i], _compilation).AlwaysFalse.ContainsCase(combination[i])).All(x => x),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => CouldMatch(or.LeftPattern, combination) || CouldMatch(or.RightPattern, combination),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => CouldMatch(and.LeftPattern, combination) && CouldMatch(and.RightPattern, combination),
        INegatedPatternOperation not => !AlwaysMatches(not.Pattern, combination),
        _ => true,
    };

    public static TupleModel? Of(ITupleOperation tuple, SyntaxNode node, SemanticModel model, Session session, CancellationToken ct)
    {
        var spaces = new List<CaseSpace>();
        foreach (var element in tuple.Elements)
        {
            if (CaseSpace.OfElement(element, model, ct) is not { } space)
            {
                return null;
            }

            spaces.Add(space);
        }

        if (!spaces.Any(space => space.IsOfTypes))
        {
            return null;
        }

        var excluded = tuple.Elements.Select((element, i) => Narrowing.ExcludedBefore(node, element, spaces[i], model, session, ct)).ToList();
        var values = tuple.Elements.Select(element => TrackedValue.From(element, session)).ToList();
        var together = values.All(v => v is not null && Narrowing.IsTrackable(v, node, model, ct))
            ? Narrowing.ExcludedTogether(node, values!, spaces, model, ct)
            : [];

        if (excluded.All(set => set.IsEmpty) && together.Count == 0)
        {
            return null;
        }

        var cases = spaces.Select(space => space.Cases.Cast<object?>().Append(null).ToList()).ToList();
        if (cases.Aggregate(1L, (count, c) => count * c.Count) > Limits.Combinations)
        {
            return null;
        }

        return new TupleModel(spaces, excluded, together, [.. Combinations.Of(cases)], model.Compilation);
    }

    public bool RuledOut(object?[] combination) =>
        _excluded.Where((set, i) => set.ContainsCase(combination[i])).Any()
        || _together.Any(box => Combinations.InBox(box, combination));
}

/// <summary>
/// Reports a switch expression arm, switch statement case label or type test over a union or closed hierarchy that can't match, because every case it could match was ruled out by code before it.
/// (One that's dead because of earlier arms or labels gets CS8510 or CS8120 already.)
/// For a switch over a tuple, every combination of cases it could match has to be ruled out (or matched by an earlier arm).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeadArmAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.DeadArm];

    /// <summary>
    /// Reports the arms (or labels) that can't match;
    /// returns them.
    /// </summary>
    private static HashSet<SyntaxNode> Analyze(OperationAnalysisContext context, IOperation input, string what, IEnumerable<(SyntaxNode Syntax, IPatternOperation Pattern, bool Guarded)> candidates, Session session)
    {
        var reported = new HashSet<SyntaxNode>();
        if (context.Operation.SemanticModel is not { } model)
        {
            return reported;
        }

        if (input is ITupleOperation tuple)
        {
            AnalyzeTuple(context, tuple, what, [.. candidates], model, session, reported);
            return reported;
        }

        if (CaseSpace.Of(input, model, context.CancellationToken) is not { } space)
        {
            return reported;
        }

        var excluded = Narrowing.ExcludedBefore(context.Operation.Syntax, input, space, model, session, context.CancellationToken);
        if (excluded.Cases.IsEmpty)
        {
            return reported;
        }

        var earlier = CaseSet.Empty;
        foreach (var (syntax, pattern, guarded) in candidates)
        {
            var outcome = Facts.Of(pattern, space, model.Compilation);

            // Only ones that can't match null either: removing one mustn't change what happens to a null.
            if (outcome.AlwaysFalse.IncludesNull)
            {
                var couldMatch = space.Cases.Where(t => !earlier.Contains(t) && !outcome.AlwaysFalse.Contains(t)).ToList();
                if (couldMatch.Count > 0 && couldMatch.All(excluded.Contains))
                {
                    var names = MissingPatterns.Names(couldMatch, space, model, syntax.SpanStart);
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.DeadArm, syntax.GetLocation(), what, names));
                    reported.Add(syntax);
                }
            }

            if (!guarded)
            {
                earlier |= outcome.AlwaysTrue;
            }
        }

        return reported;
    }

    /// <summary>
    /// Reports the arms (or case labels) that can't match.
    /// A <c>default:</c> label is tried last and matches anything, so it's never dead, and constant labels aren't reported:
    /// a <c>goto case</c> might go to one.
    /// </summary>
    private static void AnalyzeLabels(OperationAnalysisContext context, IOperation input, string what, Session session)
    {
        var labels = SwitchLabel.Of(context.Operation);
        var reported = Analyze(context, input, what, labels.Where(label => label.Pattern is not null).Select(label => (label.Syntax, label.Pattern!, Guarded: label.Guard is not null)), session);
        AnalyzeNested(context, input, what, labels, reported, session);
    }

    /// <summary>
    /// Arms (or labels) that look inside the input, at a union a case holds or a union-typed member, and can't match:
    /// what they could match was all ruled out (see <see cref="NestedCoverage.DeadArms"/>).
    /// </summary>
    private static void AnalyzeNested(OperationAnalysisContext context, IOperation input, string what, IReadOnlyList<SwitchLabel> labels, HashSet<SyntaxNode> reported, Session session)
    {
        if (context.Operation.SemanticModel is not { } model || NestedCoverage.DeadArms(input, labels, context.Operation.Syntax, model, session, context.CancellationToken) is not { } dead)
        {
            return;
        }

        foreach (var label in dead.Select(index => labels[index]))
        {
            if (!reported.Contains(label.Syntax) && label.Pattern is { } pattern)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DeadArm, label.Syntax.GetLocation(), what, pattern.Syntax.ToString()));
            }
        }
    }

    /// <summary>
    /// A type test (<c>x is Bird</c>, <c>x is Bird b</c>) on a union or closed hierarchy value that can't be true:
    /// every case it could be true for was ruled out by earlier code, and it can't be true for null (or null was ruled out too).
    /// </summary>
    private static void AnalyzeTest(OperationAnalysisContext context, Session session)
    {
        var operand = context.Operation switch
        {
            IIsPatternOperation isPattern => isPattern.Value,
            IIsTypeOperation isType => isType.ValueOperand,
            _ => null,
        };

        // `(a, b) is (Bird, _)`, where every combination it could be true for was ruled out.
        if (context.Operation is IIsPatternOperation { Value: ITupleOperation tuple, Pattern: var tuplePattern }
            && context.Operation.SemanticModel is { } tupleModel
            && TupleModel.Of(tuple, context.Operation.Syntax, tupleModel, session, context.CancellationToken) is { } combinations
        )
        {
            var reachable = combinations.All.Where(c => combinations.CouldMatch(tuplePattern, c)).ToList();
            if (reachable.Count > 0 && reachable.All(combinations.RuledOut))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DeadArm, context.Operation.Syntax.GetLocation(), "test", tuplePattern.Syntax.ToString()));
            }

            return;
        }

        if (operand is null
            || context.Operation.SemanticModel is not { } model
            || TrackedValue.From(operand, session) is not { } value
            || CaseSpace.Of(operand, model, context.CancellationToken) is not { } space
        )
        {
            return;
        }

        var excluded = Narrowing.ExcludedBefore(context.Operation.Syntax, operand, space, model, session, context.CancellationToken);
        if (excluded.Cases.IsEmpty)
        {
            return;
        }

        var outcome = Facts.Of(context.Operation, value, space, model.Compilation);
        if (!outcome.AlwaysFalse.IncludesNull && !excluded.IncludesNull)
        {
            return;
        }

        var couldBeTrue = space.Cases.Where(t => !outcome.AlwaysFalse.Contains(t)).ToList();
        if (couldBeTrue.Count > 0 && couldBeTrue.All(excluded.Contains))
        {
            var names = MissingPatterns.Names(couldBeTrue, space, model, context.Operation.Syntax.SpanStart);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DeadArm, context.Operation.Syntax.GetLocation(), "test", names));
        }
    }

    /// <summary>
    /// For a switch over a tuple literal:
    /// an arm is dead if every combination of cases (null included, for every element) that it could match is either matched by an earlier arm without a <c>when</c> clause or ruled out by earlier code, element by element or together.
    /// </summary>
    private static void AnalyzeTuple(
        OperationAnalysisContext context, ITupleOperation tuple, string what, List<(SyntaxNode Syntax, IPatternOperation Pattern, bool Guarded)> candidates, SemanticModel model, Session session, HashSet<SyntaxNode> reported
    )
    {
        if (TupleModel.Of(tuple, context.Operation.Syntax, model, session, context.CancellationToken) is not { } combinations)
        {
            return;
        }

        var earlier = new List<IPatternOperation>();
        foreach (var (syntax, pattern, guarded) in candidates)
        {
            var reachable = combinations.All.Where(c => combinations.CouldMatch(pattern, c) && !earlier.Any(e => combinations.AlwaysMatches(e, c))).ToList();
            if (reachable.Count > 0 && reachable.All(combinations.RuledOut))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DeadArm, syntax.GetLocation(), what, pattern.Syntax.ToString()));
                reported.Add(syntax);
            }

            if (!guarded)
            {
                earlier.Add(pattern);
            }
        }
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var session = new Session(start.Options.AnalyzerConfigOptionsProvider);
            start.RegisterOperationAction(c => AnalyzeLabels(c, ((ISwitchExpressionOperation)c.Operation).Value, "arm", session), OperationKind.SwitchExpression);
            start.RegisterOperationAction(c => AnalyzeLabels(c, ((ISwitchOperation)c.Operation).Value, "case", session), OperationKind.Switch);
            start.RegisterOperationAction(c => AnalyzeTest(c, session), OperationKind.IsPattern, OperationKind.IsType);
        });
    }
}

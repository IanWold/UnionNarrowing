using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What the code before a throwing default (a switch, or a run of tests on one value) handles of the value's cases.
/// The default catches the rest, apart from what earlier code rules out.
/// </summary>
internal class Handling
{
    private readonly IReadOnlyList<SwitchLabel>? _labels;
    private readonly Session _session;

    /// <summary>
    /// The cases an arm, label or test (without a <c>when</c> clause) is sure to catch.
    /// </summary>
    public CaseSet Handled { get; }

    /// <summary>
    /// The value switched on, or tested.
    /// </summary>
    public IOperation Input { get; }

    public CaseSpace Space { get; }

    /// <summary>
    /// The switch, or the first test, which is where what earlier code rules out is measured.
    /// </summary>
    public SyntaxNode Start { get; }

    /// <param name="labels">The arms, labels or tests before the default, as labels on the value, for what they handle inside its cases;
    /// null for tests that can't be written as labels.</param>
    private Handling(SyntaxNode start, IOperation input, CaseSpace space, CaseSet handled, IReadOnlyList<SwitchLabel>? labels, Session session)
    {
        _labels = labels;
        _session = session;
        Start = start;
        Input = input;
        Space = space;
        Handled = handled;
    }

    /// <summary>
    /// The tests as switch labels on <paramref name="value"/>:
    /// each has to be a pattern on it, on its own or followed by <c>&amp;&amp;</c> and more, which is then a guard, like a <c>when</c> clause.
    /// Null if one isn't.
    /// </summary>
    private static IReadOnlyList<SwitchLabel>? AsLabels(IReadOnlyList<IOperation> conditions, TrackedValue value)
    {
        var labels = new List<SwitchLabel>();
        foreach (var condition in conditions)
        {
            var test = condition;
            while (test is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } both)
            {
                test = both.LeftOperand;
            }

            if (test is not IIsPatternOperation isPattern || !value.IsReadBy(isPattern.Value))
            {
                return null;
            }

            labels.Add(new SwitchLabel(condition.Syntax, isPattern.Pattern, test == condition ? null : condition));
        }

        return labels;
    }

    /// <summary>
    /// What a run of tests on one value handles of its cases:
    /// the cases one of the conditions is sure to hold for.
    /// The value is the first one that every condition tests, with a type test or pattern, or a helper that does (as <see cref="Facts"/> reads them);
    /// null if there isn't one.
    /// A condition that writes the value says nothing about what it held when the run started, though it's still about the value.
    /// </summary>
    private static Handling? OfTests(SyntaxNode start, IReadOnlyList<IOperation> conditions, SemanticModel model, Session session, CancellationToken ct)
    {
        var tried = new HashSet<TrackedValue>();
        foreach (var tested in conditions.SelectMany(condition => condition.DescendantsAndSelf()).SelectMany(TestedOperands))
        {
            if (TrackedValue.From(tested, session) is not { } value || !tried.Add(value) || CaseSpace.Of(tested, model, ct) is not { } space)
            {
                continue;
            }

            var handled = CaseSet.Empty;
            var aboutTheValue = true;

            foreach (var condition in conditions.Where(condition => !WriteAnalysis.Writes(condition.Syntax, value, model, ct)))
            {
                var outcome = Facts.Of(condition, value, space, model.Compilation);

                aboutTheValue &= !(outcome.AlwaysTrue.IsEmpty && outcome.AlwaysFalse.IsEmpty);
                handled |= outcome.AlwaysTrue;
            }

            if (aboutTheValue)
            {
                return new Handling(start, tested, space, handled, AsLabels(conditions, value), session);
            }
        }

        return null;
    }

    /// <summary>
    /// What an operation in a condition might test:
    /// the operand of a type test or pattern, or what a call or property is given (for a helper that tests it).
    /// </summary>
    private static IEnumerable<IOperation> TestedOperands(IOperation operation) => operation switch
    {
        IIsPatternOperation isPattern => [isPattern.Value],
        IIsTypeOperation isType => [isType.ValueOperand],
        IInvocationOperation call => call.Arguments.Select(argument => argument.Value).Prepend(call.Instance).OfType<IOperation>(),
        IPropertyReferenceOperation { Instance: { } instance } => [instance],
        _ => [],
    };

    /// <summary>
    /// What a switch over a union or closed hierarchy handles apart from its default:
    /// a switch expression's last arm, or a switch statement's <c>default:</c> label.
    /// </summary>
    public static Handling? BeforeDefaultOf(SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var switchOperation = model.GetOperation(switchNode, ct);
        if (switchOperation switch { ISwitchExpressionOperation expression => expression.Value, ISwitchOperation statement => statement.Value, _ => null } is not { } input
            || CaseSpace.Of(input, model, ct) is not { } space
        )
        {
            return null;
        }

        var labels = SwitchLabel.Of(switchOperation);
        labels = switchOperation is ISwitchExpressionOperation ? [.. labels.Take(labels.Count - 1)] : [.. labels.Where(label => !label.IsDefault)];

        var handled = labels.Where(label => label.Guard is null).Aggregate(CaseSet.Empty, (set, label) => set | (label.PatternOrClause switch
        {
            IPatternOperation pattern => Facts.Of(pattern, space, model.Compilation).AlwaysTrue,
            ICaseClauseOperation clause => Facts.Of(clause, space, model.Compilation).AlwaysTrue,
            _ => CaseSet.Empty,
        }));

        return new Handling(switchNode, input, space, handled, labels, session);
    }

    /// <summary>
    /// What a throw statement comes right after, if it's a default in all but name:
    /// a switch statement over a union or closed hierarchy with no default, and no section that breaks out of it, or two or more <c>if</c>s that each test the value and exit (see <see cref="IfRunBefore"/>).
    /// </summary>
    public static Handling? BeforeThrow(ThrowStatementSyntax throwStatement, SemanticModel model, Session session, CancellationToken ct)
    {
        var statements = throwStatement.Parent switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            _ => default,
        };

        var index = statements.IndexOf(throwStatement);
        if (index <= 0)
        {
            return null;
        }

        if (statements[index - 1] is SwitchStatementSyntax switchStatement
            && model.GetOperation(switchStatement, ct) is ISwitchOperation switchOp
            && !Statements.HasDefault(switchOp)
            && !Statements.BreaksOut(switchStatement, switchStatement)
        )
        {
            return BeforeDefaultOf(switchStatement, model, session, ct);
        }

        return IfRunBefore(statements, index, model, session, ct);
    }

    /// <summary>
    /// The <c>else</c> that a chain of ifs starting at <paramref name="top"/> ends in, if it only throws (see <see cref="OnlyThrows"/>) and the chain is at least two tests on one union or closed hierarchy value:
    /// that else, and what the tests handle.
    /// </summary>
    public static (ElseClauseSyntax Else, Handling Handling)? BeforeThrowingElse(IConditionalOperation top, SemanticModel model, Session session, CancellationToken ct)
    {
        // An `else if` is an else whose statement is an if (inside an implicit block).
        static IOperation? Unwrapped(IOperation? operation) =>
            operation is IBlockOperation { IsImplicit: true, Operations.Length: 1 } block ? block.Operations[0] : operation;

        var conditions = new List<IOperation>();
        var current = top;

        while (true)
        {
            conditions.Add(current.Condition);

            if (Unwrapped(current.WhenFalse) is not IConditionalOperation { Syntax: IfStatementSyntax } next)
            {
                break;
            }

            current = next;
        }

        if (conditions.Count < 2 || current.WhenFalse is not { } last || !OnlyThrows([last]) || ((IfStatementSyntax)current.Syntax).Else is not { } elseClause)
        {
            return null;
        }

        return OfTests(top.Syntax, conditions, model, session, ct) is { } handling ? (elseClause, handling) : null;
    }

    /// <summary>
    /// The cases that get to the default:
    /// neither handled nor in <paramref name="excluded"/>.
    /// </summary>
    public List<object> Caught(CaseSet excluded) =>
        [.. Space.Cases.Where(candidate => !Handled.Contains(candidate) && !excluded.Contains(candidate))];

    /// <summary>
    /// What the default catches, as patterns:
    /// none if it catches no case;
    /// where the arms or tests look inside the value's cases (<c>Err { Error: NotFound }</c>), what they miss inside them (see <see cref="NestedCoverage.Missing"/>);
    /// otherwise the cases it catches, with null where that matters.
    /// </summary>
    public List<PatternSyntax> CaughtPatterns(SemanticModel model, CancellationToken ct)
    {
        var excluded = Excluded(model, ct);
        var caught = Caught(excluded);

        if (caught.Count == 0 || HandledInside(model, ct))
        {
            return [];
        }

        if (_labels is not null && NestedCoverage.Missing(Input, _labels, Start, model, _session, ct) is { } inside)
        {
            return inside;
        }

        var withNull = Space.NullMatters && !Handled.IncludesNull && !excluded.IncludesNull;
        
        return MissingPatterns.For(caught, withNull, Space, model, Start.SpanStart);
    }

    /// <summary>
    /// What code before <see cref="Start"/> rules out for the value.
    /// </summary>
    public CaseSet Excluded(SemanticModel model, CancellationToken ct) =>
        Narrowing.ExcludedBefore(Start, Input, Space, model, _session, ct);

    /// <summary>
    /// Whether the arms, labels or tests, looking inside the value's cases (<c>Kitten { Toy: Ball }</c>), handle every value that's left between them:
    /// then the default catches nothing, though none of them handles a whole case it catches.
    /// </summary>
    public bool HandledInside(SemanticModel model, CancellationToken ct) =>
        _labels is not null && NestedCoverage.Of(Input, _labels, Start, model, _session, ct) is (true, _);

    /// <summary>
    /// What two or more <c>if (test) &lt;exit&gt;</c> statements (or an else-if chain whose branches all exit, with no final else) right before <paramref name="index"/> handle, testing one union or closed hierarchy value.
    /// </summary>
    public static Handling? IfRunBefore(SyntaxList<StatementSyntax> statements, int index, SemanticModel model, Session session, CancellationToken ct)
    {
        var conditions = new List<IOperation>();
        var first = index;

        for (var i = index - 1; i >= 0 && statements[i] is IfStatementSyntax ifStatement; i--)
        {
            var chain = new List<IfStatementSyntax>();

            for (IfStatementSyntax? link = ifStatement; link is not null; link = link.Else?.Statement as IfStatementSyntax)
            {
                chain.Add(link);
                if (link.Else is { Statement: not IfStatementSyntax })
                {
                    return null; // a final else: the throw isn't reached from here
                }
            }

            if (chain.Any(link => model.AnalyzeControlFlow(link.Statement) is not { Succeeded: true, EndPointIsReachable: false }))
            {
                break;
            }

            foreach (var link in chain)
            {
                if (model.GetOperation(link.Condition, ct) is { } condition)
                {
                    conditions.Add(condition);
                }
            }

            first = i;
        }

        return conditions.Count < 2 ? null : OfTests(statements[first], conditions, model, session, ct);
    }

    /// <summary>
    /// Whether a section body (or an else) can only end by throwing:
    /// it gets to a <c>throw</c>, or a call that never returns (see <see cref="Facts.IsThrow"/>), with nothing before it that could leave another way (<c>log(...); throw ...;</c>, <c>return AssertNever(x);</c>).
    /// </summary>
    public static bool OnlyThrows(ImmutableArray<IOperation> body)
    {
        foreach (var statement in body)
        {
            var throws = statement switch
            {
                IExpressionStatementOperation { Operation: var expression } => Facts.IsThrow(expression),
                IReturnOperation { ReturnedValue: { } returned } => Facts.IsThrow(returned),
                IBlockOperation block => OnlyThrows(block.Operations),
                _ => Facts.IsThrow(statement),
            };

            if (throws)
            {
                return true;
            }

            if (statement.DescendantsAndSelf().Any(operation => operation is IReturnOperation or IBranchOperation))
            {
                return false;
            }
        }

        return false;
    }
}

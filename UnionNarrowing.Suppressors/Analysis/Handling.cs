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

    private Handling(SyntaxNode start, IOperation input, CaseSpace space, CaseSet handled, Session session)
    {
        _session = session;
        Start = start;
        Input = input;
        Space = space;
        Handled = handled;
    }

    /// <summary>
    /// What a run of tests handles of the value <paramref name="first"/>'s first type test or pattern is on:
    /// the cases one of the conditions is sure to hold for.
    /// A condition that writes the value says nothing about what it held when the run started.
    /// </summary>
    private static Handling? OfTests(SyntaxNode start, IReadOnlyList<IOperation> conditions, IOperation first, SemanticModel model, Session session, CancellationToken ct)
    {
        var tested = first.DescendantsAndSelf()
            .Select(c => c switch { IIsPatternOperation p => p.Value, IIsTypeOperation t => t.ValueOperand, _ => null })
            .FirstOrDefault(v => v is not null);

        if (tested is null || TrackedValue.From(tested, session) is not { } value || CaseSpace.Of(tested, model, ct) is not { } space)
        {
            return null;
        }

        var handled = conditions.Aggregate(CaseSet.Empty, (set, condition) => set | (WriteAnalysis.Writes(condition.Syntax, value, model, ct) ? CaseSet.Empty : Facts.Of(condition, value, space, model.Compilation).AlwaysTrue));

        return new Handling(start, tested, space, handled, session);
    }

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
        if (switchOperation is ISwitchExpressionOperation)
        {
            labels = [.. labels.Take(labels.Count - 1)];
        }

        var handled = labels.Where(label => label.Guard is null && !label.IsDefault).Aggregate(CaseSet.Empty, (set, label) => set | (label.PatternOrClause switch
        {
            IPatternOperation pattern => Facts.Of(pattern, space, model.Compilation).AlwaysTrue,
            ICaseClauseOperation clause => Facts.Of(clause, space, model.Compilation).AlwaysTrue,
            _ => CaseSet.Empty,
        }));

        return new Handling(switchNode, input, space, handled, session);
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
    /// The <c>else</c> that a chain of ifs starting at <paramref name="top"/> ends in, if it only throws and the chain is at least two tests on one union or closed hierarchy value:
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

        return OfTests(top.Syntax, conditions, conditions[0], model, session, ct) is { } handling ? (elseClause, handling) : null;
    }

    /// <summary>
    /// The cases that get to the default:
    /// neither handled nor in <paramref name="excluded"/>.
    /// </summary>
    public List<object> Caught(CaseSet excluded) =>
        [.. Space.Cases.Where(candidate => !Handled.Contains(candidate) && !excluded.Contains(candidate))];

    /// <summary>
    /// What code before <see cref="Start"/> rules out for the value.
    /// </summary>
    public CaseSet Excluded(SemanticModel model, CancellationToken ct) =>
        Narrowing.ExcludedBefore(Start, Input, Space, model, _session, ct);

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

        return conditions.Count < 2 ? null : OfTests(statements[first], conditions, conditions[conditions.Count - 1], model, session, ct);
    }

    /// <summary>
    /// Whether a section body (or an else) does nothing but throw.
    /// </summary>
    public static bool OnlyThrows(ImmutableArray<IOperation> body) => body.Length == 1 && body[0] switch
    {
        IThrowOperation => true,
        IExpressionStatementOperation { Operation: IThrowOperation } => true,
        IBlockOperation block => OnlyThrows(block.Operations),
        _ => false,
    };
}

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// An arm of a switch expression, or a case label of a switch statement, in the order they're tried (except that a <c>default:</c> label is tried last, wherever it is).
/// </summary>
internal readonly struct SwitchLabel(SyntaxNode syntax, IOperation patternOrClause, IOperation? guard)
{
    /// <summary>
    /// The <c>when</c> clause, if there is one.
    /// </summary>
    public IOperation? Guard { get; } = guard;

    public bool IsDefault =>
        PatternOrClause is IDefaultCaseClauseOperation;

    /// <summary>
    /// The pattern an arm or a pattern label tests (not a constant label's, or <c>default:</c>).
    /// </summary>
    public IPatternOperation? Pattern => PatternOrClause switch
    {
        IPatternOperation pattern => pattern,
        IPatternCaseClauseOperation clause => clause.Pattern,
        _ => null,
    };

    /// <summary>
    /// An arm's pattern, or the label's case clause.
    /// </summary>
    public IOperation PatternOrClause { get; } = patternOrClause;

    /// <summary>
    /// The arm, or the label.
    /// </summary>
    public SyntaxNode Syntax { get; } = syntax;

    /// <summary>
    /// The arms of a switch expression, or the case labels of a switch statement;
    /// none for anything else.
    /// </summary>
    public static IReadOnlyList<SwitchLabel> Of(IOperation? switchOperation) => switchOperation switch
    {
        ISwitchExpressionOperation expression => expression.Arms.Select(arm => new SwitchLabel(arm.Syntax, arm.Pattern, arm.Guard)).ToList(),
        ISwitchOperation statement => [.. statement.Cases.SelectMany(c => c.Clauses).Select(clause => new SwitchLabel(clause.Syntax, clause, (clause as IPatternCaseClauseOperation)?.Guard))],
        _ => [],
    };
}

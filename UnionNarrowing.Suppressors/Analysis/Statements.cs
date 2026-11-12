using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// How statements sit in their lists, and how jumps get out of them.
/// </summary>
internal static class Statements
{
    /// <summary>
    /// The <c>break</c>s in <paramref name="part"/> (outside any function inside it) that end <paramref name="target"/>.
    /// </summary>
    public static IEnumerable<BreakStatementSyntax> BreaksEnding(SyntaxNode part, StatementSyntax target) =>
        part.DescendantNodes(n => !Functions.IsFunction(n)).OfType<BreakStatementSyntax>().Where(b => BreakTarget(b) == target);

    /// <summary>
    /// Whether a <c>break</c> somewhere in <paramref name="part"/> ends <paramref name="target"/> (a loop or switch statement).
    /// </summary>
    public static bool BreaksOut(SyntaxNode part, StatementSyntax target) =>
        BreaksEnding(part, target).Any();

    /// <summary>
    /// The loop or switch statement a <c>break</c> ends, or null if it isn't in one (which doesn't compile, but the IDE still analyzes it).
    /// </summary>
    public static StatementSyntax? BreakTarget(BreakStatementSyntax breakStatement) =>
        breakStatement.Ancestors().OfType<StatementSyntax>().FirstOrDefault(a => a is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or SwitchStatementSyntax);

    /// <summary>
    /// Whether a <c>goto case</c> or <c>goto default</c>, or a <c>goto</c> to a label in another section, could enter a section of the switch statement without its labels matching.
    /// </summary>
    public static bool GotosIntoSections(SwitchStatementSyntax switchStatement)
    {
        SwitchSectionSyntax? SectionOf(SyntaxNode node) =>
            node.Ancestors().OfType<SwitchSectionSyntax>().FirstOrDefault(section => section.Parent == switchStatement);

        return switchStatement.DescendantNodes().OfType<GotoStatementSyntax>().Any(jump =>
            !jump.IsKind(SyntaxKind.GotoStatement)
            || (jump.Expression is IdentifierNameSyntax target
                && switchStatement.DescendantNodes().OfType<LabeledStatementSyntax>().Any(label => label.Identifier.ValueText == target.Identifier.ValueText && SectionOf(label) != SectionOf(jump))
            )
        );
    }

    /// <summary>
    /// Whether a label of the switch statement is <c>default:</c>.
    /// </summary>
    public static bool HasDefault(ISwitchOperation switchOp) =>
        switchOp.Cases.SelectMany(c => c.Clauses).Any(clause => clause is IDefaultCaseClauseOperation);

    public static int IndexOf(IReadOnlyList<StatementSyntax> statements, SyntaxNode statement)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] == statement)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The statement list that the child of <paramref name="parent"/> sits in directly, if it holds one:
    /// a block's statements, a switch section's, or the program's top-level statements.
    /// </summary>
    public static IReadOnlyList<StatementSyntax>? StatementsAround(SyntaxNode parent) => parent switch
    {
        BlockSyntax block => block.Statements,
        SwitchSectionSyntax section => section.Statements,
        GlobalStatementSyntax { Parent: CompilationUnitSyntax unit } => [.. unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement)],
        _ => null,
    };
}

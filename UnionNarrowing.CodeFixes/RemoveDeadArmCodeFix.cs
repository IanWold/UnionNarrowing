using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Takes arms and case labels out of their switches, all at once, innermost switch first.
/// The directives before each one stay, with any inactive code between them, before whatever comes next.
/// </summary>
file class Remover : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation Dead = new(nameof(Remover));

    /// <summary>
    /// <paramref name="node"/>, after the <paramref name="directives"/> from what was taken out before it.
    /// </summary>
    private static T After<T>(SyntaxTriviaList directives, T node) where T : SyntaxNode =>
        directives.Count == 0 ? node : node.WithLeadingTrivia(directives.AddRange(node.GetLeadingTrivia()));

    /// <summary>
    /// The directives before a node that's taken out, which stay: its leading trivia up to its last directive, with what's between them.
    /// </summary>
    private static SyntaxTriviaList DirectivesBefore(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia();
        return TriviaList(leading.Take(leading.Count - leading.Reverse().TakeWhile(t => !t.IsDirective).Count()));
    }

    /// <summary>
    /// Whether there are directives in <paramref name="node"/> past its leading trivia, which the code after it could depend on.
    /// </summary>
    private static bool HasDirectivesInside(SyntaxNode node) =>
        node.WithoutLeadingTrivia().ContainsDirectives;

    /// <summary>
    /// Whether <paramref name="section"/> has to stay even when none of its labels can match:
    /// it declares locals other sections can use (a switch block is one scope), or has directives inside.
    /// Then it keeps one label, as fixing its labels one at a time would.
    /// </summary>
    private static bool MustStay(SwitchSectionSyntax section) =>
        section.Statements.Any(s => s is LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax) || HasDirectivesInside(section);

    /// <summary>
    /// Whether an arm or case label can be taken out (with its section, if it's the section's only label):
    /// not if that would take out directives inside it, or a section that <see cref="MustStay">has to stay</see>.
    /// </summary>
    public static bool CanRemove(SyntaxNode node) =>
        node is SwitchLabelSyntax { Parent: SwitchSectionSyntax { Labels.Count: 1 } section }
            ? !MustStay(section)
            : node is SwitchExpressionArmSyntax or SwitchLabelSyntax && !HasDirectivesInside(node);

    /// <summary>
    /// Removes the arms and case labels among <paramref name="nodes"/> that <see cref="CanRemove">can be taken out</see>.
    /// </summary>
    public static SyntaxNode Remove(SyntaxNode root, IEnumerable<SyntaxNode> nodes)
    {
        var dead = nodes.Where(CanRemove).ToList();
        return dead.Count == 0 ? root : new Remover().Visit(root.ReplaceNodes(dead, (_, node) => node.WithAdditionalAnnotations(Dead)));
    }

    /// <summary>
    /// Each arm that's kept keeps the comma after it, except that the last one only has a comma if the switch ended in one.
    /// Without one, the last arm ends with what followed its comma where that ends the line (a comment, say), and otherwise with what followed the old last arm.
    /// It keeps its comma when directives from the arms after it are left before the closing brace, since they can bring in arms of their own.
    /// </summary>
    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var visited = (SwitchExpressionSyntax)base.VisitSwitchExpression(node)!;
        var arms = visited.Arms;
        if (!arms.Any(arm => arm.HasAnnotation(Dead)) || arms.All(arm => arm.HasAnnotation(Dead)))
        {
            return visited;
        }

        var list = new List<SyntaxNodeOrToken>();
        var directives = SyntaxTriviaList.Empty;

        for (var i = 0; i < arms.Count; i++)
        {
            if (arms[i].HasAnnotation(Dead))
            {
                directives = directives.AddRange(DirectivesBefore(arms[i]));
                continue;
            }

            list.Add(After(directives, arms[i]));
            directives = SyntaxTriviaList.Empty;

            if (i < arms.SeparatorCount)
            {
                list.Add(arms.GetSeparator(i));
            }
        }

        var trailingComma = arms.SeparatorCount == arms.Count;
        if (!trailingComma && list[list.Count - 1].IsToken && directives.Count == 0)
        {
            var comma = list[list.Count - 1].AsToken();
            list.RemoveAt(list.Count - 1);
            var ending = comma.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) ? comma.TrailingTrivia : arms[arms.Count - 1].GetTrailingTrivia();
            list[list.Count - 1] = list[list.Count - 1].AsNode()!.WithTrailingTrivia(ending);
        }

        return visited
            .WithArms(SeparatedList<SwitchExpressionArmSyntax>(list))
            .WithCloseBraceToken(visited.CloseBraceToken.WithLeadingTrivia(directives.AddRange(visited.CloseBraceToken.LeadingTrivia)));
    }

    /// <summary>
    /// A section none of whose labels can match goes, unless it <see cref="MustStay">has to stay</see>.
    /// The directives before a label that goes are left before the next label, or before the section's statements if it was the last.
    /// </summary>
    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
        if (!visited.Sections.Any(s => s.Labels.Any(l => l.HasAnnotation(Dead))))
        {
            return visited;
        }

        var sections = new List<SwitchSectionSyntax>();
        var directives = SyntaxTriviaList.Empty;
        foreach (var section in visited.Sections)
        {
            var allDead = section.Labels.All(label => label.HasAnnotation(Dead));
            if (allDead && !MustStay(section))
            {
                directives = directives.AddRange(DirectivesBefore(section));
                continue;
            }

            var labels = new List<SwitchLabelSyntax>();
            for (var i = 0; i < section.Labels.Count; i++)
            {
                var label = section.Labels[i];
                if (label.HasAnnotation(Dead) && !(allDead && i == section.Labels.Count - 1))
                {
                    directives = directives.AddRange(DirectivesBefore(label));
                    continue;
                }

                labels.Add(After(directives, label));
                directives = SyntaxTriviaList.Empty;
            }

            var statements = section.Statements;
            sections.Add(section.WithLabels(List(labels)).WithStatements(statements.Count == 0 ? statements : statements.Replace(statements[0], After(directives, statements[0]))));
            directives = SyntaxTriviaList.Empty;
        }

        return visited
            .WithSections(List(sections))
            .WithCloseBraceToken(visited.CloseBraceToken.WithLeadingTrivia(directives.AddRange(visited.CloseBraceToken.LeadingTrivia)));
    }
}

/// <summary>
/// Removes a switch arm or case label that can't match (UN0004), or a throwing default arm that only catches cases added later (UN0016).
/// </summary>
/// <remarks>
/// Fix All removes everything it's given in one pass, rather than merging what each fix would do on its own:
/// two arms removed from one switch share a comma, and two labels removed from one section would each leave the other, so the separate edits collide.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveDeadArmCodeFix)), Shared]
public sealed class RemoveDeadArmCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.DeadArm.Id, Descriptors.ThrowingDefaultOnCompleteSwitch.Id];

    private static async Task<Document> RemoveAsync(Document document, SyntaxNode node, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        return document.WithSyntaxRoot(Remover.Remove(root!, [node]));
    }

    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) =>
        {
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            return root is null ? null : document.WithSyntaxRoot(Remover.Remove(root, diagnostics.Select(d => root.FindNode(d.Location.SourceSpan))));
        });

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span) is { } node && Remover.CanRemove(node))
        {
            context.RegisterCodeFix(
                CodeAction.Create(node is SwitchExpressionArmSyntax ? "Remove the arm" : "Remove the case", ct => RemoveAsync(context.Document, node, ct), equivalenceKey: nameof(RemoveDeadArmCodeFix)),
                context.Diagnostics
            );
        }
    }
}

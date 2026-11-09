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
/// </summary>
file class Remover : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation Dead = new(nameof(Remover));

    /// <summary>
    /// Whether <paramref name="section"/> declares locals, which other sections can use (a switch block is one scope).
    /// </summary>
    public static bool DeclaresLocals(SwitchSectionSyntax section) =>
        section.Statements.Any(s => s is LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax);

    /// <summary>
    /// Removes the arms and case labels among <paramref name="nodes"/>.
    /// </summary>
    public static SyntaxNode Remove(SyntaxNode root, IEnumerable<SyntaxNode> nodes)
    {
        var dead = nodes.Where(n => n is SwitchExpressionArmSyntax or SwitchLabelSyntax).ToList();
        return dead.Count == 0 ? root : new Remover().Visit(root.ReplaceNodes(dead, (_, node) => node.WithAdditionalAnnotations(Dead)));
    }

    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var visited = (SwitchExpressionSyntax)base.VisitSwitchExpression(node)!;
        var arms = visited.Arms;
        var kept = arms.Where(arm => !arm.HasAnnotation(Dead)).ToList();
        if (kept.Count == arms.Count || kept.Count == 0)
        {
            return visited;
        }

        // Each arm that's kept keeps the comma after it, except that the last one only has a comma if the switch
        // ended in one. Without one, the last arm gets what followed the old last arm: the space or line break
        // before the closing brace.
        var trailingComma = arms.SeparatorCount == arms.Count;
        var list = new List<SyntaxNodeOrToken>();
        for (var i = 0; i < kept.Count; i++)
        {
            var index = arms.IndexOf(kept[i]);
            if (i < kept.Count - 1 || trailingComma)
            {
                list.Add(kept[i]);
                list.Add(arms.GetSeparator(index));
            }
            else
            {
                list.Add(index == arms.Count - 1 ? kept[i] : kept[i].WithTrailingTrivia(arms[arms.Count - 1].GetTrailingTrivia()));
            }
        }

        return visited.WithArms(SeparatedList<SwitchExpressionArmSyntax>(list));
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
        if (!visited.Sections.Any(s => s.Labels.Any(l => l.HasAnnotation(Dead))))
        {
            return visited;
        }

        var sections = new List<SwitchSectionSyntax>();
        foreach (var section in visited.Sections)
        {
            var kept = section.Labels.Where(label => !label.HasAnnotation(Dead)).ToList();
            if (kept.Count == section.Labels.Count)
            {
                sections.Add(section);
            }
            else if (kept.Count > 0)
            {
                sections.Add(section.WithLabels(List(kept)));
            }

            // A section none of whose labels can match goes, unless it declares locals other sections can use.
            // Then it keeps one label, as fixing its labels one at a time would.
            else if (DeclaresLocals(section))
            {
                sections.Add(section.WithLabels(SingletonList(section.Labels.Last())));
            }
        }

        return visited.WithSections(List(sections));
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
        switch (root?.FindNode(context.Span))
        {
            case SwitchExpressionArmSyntax arm:
                context.RegisterCodeFix(
                    CodeAction.Create("Remove the arm", ct => RemoveAsync(context.Document, arm, ct), equivalenceKey: nameof(RemoveDeadArmCodeFix)),
                    context.Diagnostics
                );
                break;

            case SwitchLabelSyntax { Parent: SwitchSectionSyntax section } label when section.Labels.Count > 1:
                context.RegisterCodeFix(
                    CodeAction.Create("Remove the case", ct => RemoveAsync(context.Document, label, ct), equivalenceKey: nameof(RemoveDeadArmCodeFix)),
                    context.Diagnostics
                );
                break;

            // The section's only label: the section can't run either. Unless it declares locals, which other sections
            // can use (a switch block is one scope).
            case SwitchLabelSyntax { Parent: SwitchSectionSyntax { Parent: SwitchStatementSyntax } section } label when !Remover.DeclaresLocals(section):
                context.RegisterCodeFix(
                    CodeAction.Create("Remove the case", ct => RemoveAsync(context.Document, label, ct), equivalenceKey: nameof(RemoveDeadArmCodeFix)),
                    context.Diagnostics
                );
                break;
        }
    }
}

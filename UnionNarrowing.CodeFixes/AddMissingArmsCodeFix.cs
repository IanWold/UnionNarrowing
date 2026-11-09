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
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using UnionNarrowing.Suppressors.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Adds an arm for each case a switch over a union or closed hierarchy doesn't handle (CS8509), leaving out cases earlier code already ruled out.
/// Each new arm throws <see cref="System.NotImplementedException"/> until it's filled in.
/// Also for CS8846, which is CS8509 when an arm with a <c>when</c> clause might cover what's missing, and for CS8655 and CS8847, the same two for null alone, where the arm it adds is the <c>null</c> arm.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingArmsCodeFix)), Shared]
public sealed class AddMissingArmsCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["CS8509", "CS8846", "CS8655", "CS8847"];

    private static async Task<Document> AddArmsAsync(Document document, SwitchExpressionSyntax switchExpr, IReadOnlyList<PatternSyntax> cases, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var arms = switchExpr.Arms;

        // Lay the new arms out like the existing ones: one per line if the arms start on their own line, otherwise
        // inline.
        var lineBreak = switchExpr.OpenBraceToken.TrailingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var multiline = lineBreak.IsKind(SyntaxKind.EndOfLineTrivia);
        var indentation = multiline ? arms[0].GetLeadingTrivia() : SyntaxTriviaList.Empty;
        var separatorTrivia = multiline ? TriviaList(lineBreak) : TriviaList(Space);

        var elements = arms.GetWithSeparators();
        var last = elements[elements.Count - 1];
        var tail = last.GetTrailingTrivia();
        var hadTrailingComma = last.IsToken;
        var list = elements.Replace(last, hadTrailingComma ? last.AsToken().WithTrailingTrivia(separatorTrivia) : last.AsNode()!.WithoutTrailingTrivia());
        if (!hadTrailingComma)
        {
            list = list.Add(Token(SyntaxKind.CommaToken).WithTrailingTrivia(separatorTrivia));
        }

        for (var i = 0; i < cases.Count; i++)
        {
            var arm = NewArm(cases[i]).WithLeadingTrivia(indentation);
            var isLast = i == cases.Count - 1;
            if (isLast && !hadTrailingComma)
            {
                list = list.Add(arm.WithTrailingTrivia(tail));
            }
            else
            {
                list = list.Add(arm);
                list = list.Add(Token(SyntaxKind.CommaToken).WithTrailingTrivia(isLast ? tail : separatorTrivia));
            }
        }

        var newSwitch = switchExpr.WithArms(SeparatedList<SwitchExpressionArmSyntax>(list));
        var newDocument = document.WithSyntaxRoot(root!.ReplaceNode(switchExpr, newSwitch));
        return await Simplifier.ReduceAsync(newDocument, Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    private static SwitchExpressionArmSyntax NewArm(PatternSyntax pattern)
    {
        var arm = SwitchExpressionArm(pattern, ThrowExpression(CodeFixSyntax.NewException("System.NotImplementedException")));
        return arm.NormalizeWhitespace();
    }

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var ct = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        var session = new Session(context.Document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        if (root.FindToken(context.Span.Start).Parent is not SwitchExpressionSyntax { Arms.Count: > 0 } switchExpr)
        {
            return;
        }

        // Where the arms look inside the input, the missing cases written the same way (`Kitten { Toy: Ball }`).
        var coverage = SwitchCoverage.Of(switchExpr, model, session, ct, countGuarded: false);
        var nested = model.GetOperation(switchExpr, ct) is ISwitchExpressionOperation switchOp
            ? NestedCoverage.Missing(switchOp.Value, SwitchLabel.Of(switchOp), switchExpr, model, session, ct)
            : null;
        if (nested is not { Count: > 0 } && (!coverage.OverCases || coverage.Missing.Count == 0))
        {
            return;
        }

        var patterns = nested is { Count: > 0 } ? nested : MissingPatterns.Of(coverage, model, switchExpr.SpanStart);
        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add arms for {string.Join(", ", patterns.Select(p => p.ToString()))}",
                ct => AddArmsAsync(context.Document, switchExpr, patterns, ct),
                equivalenceKey: nameof(AddMissingArmsCodeFix)
            ),
            context.Diagnostics
        );
    }
}

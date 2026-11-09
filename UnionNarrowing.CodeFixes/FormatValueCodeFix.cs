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
using UnionNarrowing.Suppressors.Analysis;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Formats what a union holds rather than the union (UN0015):
/// <c>pet</c> becomes <c>pet.Value</c>, <c>pet.ToString()</c> becomes <c>pet.Value?.ToString()</c>, and a sequence <c>pets</c> becomes <c>pets.Select(x =&gt; x.Value)</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FormatValueCodeFix)), Shared]
public sealed class FormatValueCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.UnionAsText.Id];

    private static async Task<Document> FixAsync(Document document, ExpressionSyntax expression, bool sequence, bool addLinq, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (sequence)
        {
            var values = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, CodeFixSyntax.Receiver(expression.WithoutTrivia()), IdentifierName("Select")),
                ArgumentList(SingletonSeparatedList(Argument(SimpleLambdaExpression(Parameter(Identifier("x")),
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("x"), IdentifierName("Value"))
                ))))
            );
            var newRoot = root!.ReplaceNode(expression, values.WithTriviaFrom(expression));
            if (addLinq && newRoot is CompilationUnitSyntax unit)
            {
                var lineBreak = unit.DescendantTrivia().FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
                var linq = UsingDirective(ParseName("System.Linq")).WithTrailingTrivia(lineBreak.IsKind(SyntaxKind.EndOfLineTrivia) ? lineBreak : ElasticCarriageReturnLineFeed);
                newRoot = unit.AddUsings(linq);
            }
            return document.WithSyntaxRoot(newRoot);
        }

        ExpressionSyntax replacement = expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } toString, ArgumentList.Arguments.Count: 0 }
            ? ConditionalAccessExpression(
                CodeFixSyntax.ValueOf(toString.Expression),
                InvocationExpression(MemberBindingExpression(IdentifierName("ToString")))
            )
            : CodeFixSyntax.ValueOf(expression);

        return document.WithSyntaxRoot(root!.ReplaceNode(expression, replacement.WithTriviaFrom(expression)));
    }

    private static bool IsSequenceOfUnions(ITypeSymbol type) =>
        (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } enumerable ? [enumerable] : type.AllInterfaces.AsEnumerable())
            .Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T && i.TypeArguments[0].IsUnion);

    /// <summary>
    /// Whether LINQ's Select can already be called on a <paramref name="type"/> at <paramref name="position"/>:
    /// through a using in the file, in an enclosing namespace, or a global one anywhere.
    /// </summary>
    private static bool SelectInScope(SemanticModel model, ITypeSymbol type, int position) =>
        model.LookupSymbols(position, type, "Select", includeReducedExtensionMethods: true)
            .Any(symbol => symbol is IMethodSymbol method && Symbols.Is(method.ContainingType, "Enumerable", 0, "System", "Linq"));

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span, getInnermostNodeForTie: true) is not ExpressionSyntax expression)
        {
            return;
        }

        while (expression.Span != context.Span && expression.Parent is ExpressionSyntax parent && parent.Span == context.Span)
        {
            expression = parent;
        }

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var type = model?.GetTypeInfo(expression, context.CancellationToken).Type;
        var sequence = type is { IsUnion: false } && IsSequenceOfUnions(type);
        var addLinq = sequence && !SelectInScope(model!, type!, expression.SpanStart);

        context.RegisterCodeFix(
            CodeAction.Create(sequence ? "Format their Values" : "Format its Value", ct => FixAsync(context.Document, expression, sequence, addLinq, ct), equivalenceKey: nameof(FormatValueCodeFix)),
            context.Diagnostics
        );
    }
}

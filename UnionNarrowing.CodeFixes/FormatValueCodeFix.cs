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
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using UnionNarrowing.Suppressors.Analysis;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Formats what a union holds rather than the union (UN0015):
/// <c>pet</c> becomes <c>pet.Value</c>, <c>pet.ToString()</c> becomes <c>pet.Value?.ToString() ?? ""</c>, and a sequence <c>pets</c> becomes <c>pets.Select(x =&gt; x.Value)</c>.
/// </summary>
/// <remarks>
/// Fix All formats every Value in one pass, and adds a using for LINQ, where a sequence needs one, once:
/// merged, what each fix does on its own would add the using once per fix, and the copies collide.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FormatValueCodeFix)), Shared]
public sealed class FormatValueCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.UnionAsText.Id];

    /// <summary>
    /// Formats the Values in place of what the diagnostics are on, with a using for LINQ if a sequence needs one and the file has none.
    /// </summary>
    private static async Task<Document> FixAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return document;
        }

        var formatted = diagnostics.Select(d => Formatted(root, d.Location.SourceSpan)).OfType<ExpressionSyntax>().Distinct().ToList();
        var sequences = formatted.Where(expression => SequenceType(expression, model, ct) is not null).ToList();
        var nullable = formatted.Where(expression => OfNullableUnion(expression, model, ct)).ToList();
        var newRoot = root.ReplaceNodes(formatted, (original, current) => Replacement(original, current, sequences.Contains(original), nullable.Contains(original)).WithTriviaFrom(original));

        if (newRoot is CompilationUnitSyntax unit && sequences.Any(sequence => !SelectInScope(model, SequenceType(sequence, model, ct)!, sequence.SpanStart)))
        {
            var lineBreak = unit.DescendantTrivia().FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
            newRoot = unit.AddUsings(UsingDirective(ParseName("System.Linq")).WithTrailingTrivia(lineBreak.IsKind(SyntaxKind.EndOfLineTrivia) ? lineBreak : ElasticCarriageReturnLineFeed));
        }

        return await Simplifier.ReduceAsync(document.WithSyntaxRoot(newRoot), Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether what's formatted is a nullable union, or a sequence of them, whose Value is the union rather than what it holds.
    /// </summary>
    private static bool OfNullableUnion(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        var formatted = expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } toString } ? toString.Expression : expression;
        return model.GetTypeInfo(formatted, ct).Type is { } type
            && Symbols.UnderlyingOfNullable((type.IsUnion ? null : UnionElementOf(type)) ?? type) is not null;
    }

    /// <summary>
    /// What's formatted, with its Value in place of the union (<paramref name="current"/> is <paramref name="original"/> as it is so far).
    /// </summary>
    private static ExpressionSyntax Replacement(ExpressionSyntax original, ExpressionSyntax current, bool sequence, bool nullable) =>
        sequence
            ? InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, CodeFixSyntax.Receiver(current.WithoutTrivia()), IdentifierName("Select")),
                ArgumentList(SingletonSeparatedList(Argument(SimpleLambdaExpression(Parameter(Identifier("x")), ValueOf(IdentifierName("x"), nullable)))))
            )
            : current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } toString, ArgumentList.Arguments.Count: 0 }
                ? ValueText(original, toString.Expression, nullable)
                : ValueOf(current, nullable);

    /// <summary>
    /// What a union holds: its Value, or for a nullable union (whose Value is the union), <c>?.Value</c>.
    /// </summary>
    private static ExpressionSyntax ValueOf(ExpressionSyntax union, bool nullable) =>
        nullable ? ConditionalAccessExpression(CodeFixSyntax.Receiver(union.WithoutTrivia()), MemberBindingExpression(IdentifierName("Value"))) : CodeFixSyntax.ValueOf(union);

    /// <summary>
    /// Whether LINQ's Select can already be called on a <paramref name="type"/> at <paramref name="position"/>:
    /// through a using in the file, in an enclosing namespace, or a global one anywhere.
    /// </summary>
    private static bool SelectInScope(SemanticModel model, ITypeSymbol type, int position) =>
        model.LookupSymbols(position, type, "Select", includeReducedExtensionMethods: true)
            .Any(symbol => symbol is IMethodSymbol method && Symbols.Is(method.ContainingType, "Enumerable", 0, "System", "Linq"));

    /// <summary>
    /// For a sequence of unions, its type.
    /// </summary>
    private static ITypeSymbol? SequenceType(ExpressionSyntax expression, SemanticModel model, CancellationToken ct) =>
        model.GetTypeInfo(expression, ct).Type is { IsUnion: false } type && UnionElementOf(type) is not null ? type : null;

    /// <summary>
    /// <c>union.Value?.ToString() ?? ""</c> for <c>union.ToString()</c>, which is never null, and what it's in may count on that.
    /// Without the <c>?? ""</c> where what it's in deals with null already (<c>?? "none"</c>, <c>?.Length</c>, an interpolation, which shows null as nothing).
    /// </summary>
    private static ExpressionSyntax ValueText(ExpressionSyntax toStringCall, ExpressionSyntax union, bool nullable)
    {
        var text = ConditionalAccessExpression(ValueOf(union, nullable), InvocationExpression(MemberBindingExpression(IdentifierName("ToString"))));
        return toStringCall.Parent switch
        {
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce when coalesce.Left == toStringCall => text,
            ConditionalAccessExpressionSyntax access when access.Expression == toStringCall => text,
            InterpolationSyntax => text,
            _ => ParenthesizedExpression(
                BinaryExpression(SyntaxKind.CoalesceExpression, text, Token(TriviaList(Space), SyntaxKind.QuestionQuestionToken, TriviaList(Space)), LiteralExpression(SyntaxKind.StringLiteralExpression, Literal("")))
            ).WithAdditionalAnnotations(Simplifier.Annotation),
        };
    }

    /// <summary>
    /// The expression UN0015 is reported on.
    /// </summary>
    internal static ExpressionSyntax? Formatted(SyntaxNode root, TextSpan span)
    {
        if (root.FindNode(span, getInnermostNodeForTie: true) is not ExpressionSyntax expression)
        {
            return null;
        }

        while (expression.Span != span && expression.Parent is ExpressionSyntax parent && parent.Span == span)
        {
            expression = parent;
        }

        return expression;
    }

    /// <summary>
    /// For a sequence of unions (or of nullable ones), its element type.
    /// </summary>
    internal static ITypeSymbol? UnionElementOf(ITypeSymbol type) =>
        (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } enumerable ? [enumerable] : type.AllInterfaces.AsEnumerable())
            .Where(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            .Select(i => i.TypeArguments[0])
            .FirstOrDefault(element => (Symbols.UnderlyingOfNullable(element) ?? element).IsUnion);

    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) => await FixAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false));

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        
        if (root is null || model is null || Formatted(root, context.Span) is not { } expression)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                SequenceType(expression, model, context.CancellationToken) is null ? "Format its Value" : "Format their Values",
                ct => FixAsync(context.Document, context.Diagnostics, ct),
                equivalenceKey: nameof(FormatValueCodeFix)
            ),
            context.Diagnostics
        );
    }
}

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
/// <c>pet</c> becomes <c>pet.Value</c>, <c>pet.ToString()</c> becomes <c>pet.Value?.ToString() ?? ""</c>, <c>pet.GetType().Name</c> becomes <c>pet.Value?.GetType().Name ?? ""</c>, and a sequence <c>pets</c> becomes <c>pets.Select(x =&gt; x.Value)</c>.
/// Not offered where that doesn't compile: passed to a parameter of the union's type (a <c>[LoggerMessage]</c> method's, say), which an <c>object</c> can't be, or with <c>?.</c> in a lambda that's an expression tree.
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
    /// Whether <paramref name="expression"/> calls ToString with no arguments:
    /// <c>pet.ToString()</c>, where <paramref name="receiver"/> is <c>pet</c>, or <c>?.ToString()</c> in a conditional access (<c>maybe?.ToString()</c>), where it's null.
    /// </summary>
    private static bool CallsToString(ExpressionSyntax expression, out ExpressionSyntax? receiver)
    {
        receiver = expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ? access.Expression : null;
        return expression is InvocationExpressionSyntax
        {
            ArgumentList.Arguments.Count: 0,
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } or MemberBindingExpressionSyntax { Name.Identifier.ValueText: "ToString" },
        };
    }

    /// <summary>
    /// Whether what a call to ToString is in deals with null already:
    /// <c>?? "none"</c>, <c>?.Length</c>, an interpolation (which shows null as nothing), or a conditional access it's part of (<c>order?.Status.ToString()</c>), which can be null anyway.
    /// </summary>
    private static bool DealsWithNull(ExpressionSyntax toStringCall) =>
        CodeFixSyntax.InConditionalAccess(toStringCall)
        || toStringCall.Parent switch
        {
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce => coalesce.Left == toStringCall,
            ConditionalAccessExpressionSyntax access => access.Expression == toStringCall,
            InterpolationSyntax => true,
            _ => false,
        };

    /// <summary>
    /// Whether formatting the Value in place of <paramref name="formatted"/> compiles where it is.
    /// </summary>
    private static Task<bool> CompilesAsync(Document document, SemanticModel model, ExpressionSyntax formatted, CancellationToken ct)
    {
        var sequenceType = SequenceType(formatted, model, ct);
        var replacement = Replacement(formatted, formatted, sequenceType is not null, OfNullableUnion(formatted, model, ct)).WithTriviaFrom(formatted);
        
        return CodeFixSyntax.CompilesAsync(document, formatted, replacement, ct, sequenceType is not null && !SelectInScope(model, sequenceType, formatted.SpanStart) ? WithUsingForLinq : null);
    }

    /// <summary>
    /// Formats the Values in place of what the diagnostics are on, where that compiles, with a using for LINQ if a sequence needs one and the file has none.
    /// </summary>
    private static async Task<Document> FixAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return document;
        }

        var formatted = new List<ExpressionSyntax>();
        foreach (var expression in diagnostics.Select(d => Formatted(root, d.Location.SourceSpan)).OfType<ExpressionSyntax>().Select(e => TypeOfChain(e) ?? e).Distinct())
        {
            if (await CompilesAsync(document, model, expression, ct).ConfigureAwait(false))
            {
                formatted.Add(expression);
            }
        }

        var sequences = formatted.Where(expression => SequenceType(expression, model, ct) is not null).ToList();
        var nullable = formatted.Where(expression => OfNullableUnion(expression, model, ct)).ToList();
        var newRoot = root.ReplaceNodes(formatted, (original, current) => Replacement(original, current, sequences.Contains(original), nullable.Contains(original)).WithTriviaFrom(original));

        if (sequences.Any(sequence => !SelectInScope(model, SequenceType(sequence, model, ct)!, sequence.SpanStart)))
        {
            newRoot = WithUsingForLinq(newRoot);
        }

        return await Simplifier.ReduceAsync(document.WithSyntaxRoot(newRoot), Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What holds the union that's formatted:
    /// what ToString is called on, the member a conditional access gets (<c>.Status</c> in <c>order?.Status</c>), or else the expression itself.
    /// Null for <c>?.ToString()</c>, which is called on what the access is on, and that's never a nullable union, since <c>?.</c> takes what it holds.
    /// </summary>
    private static ExpressionSyntax? Held(ExpressionSyntax formatted) =>
        formatted is ConditionalAccessExpressionSyntax access
        ? Held(access.WhenNotNull)
        : CallsToString(formatted, out var receiver)
            ? receiver
            : formatted;

    /// <summary>
    /// Whether what's formatted is a nullable union, or a sequence of them, whose Value is the union rather than what it holds.
    /// </summary>
    private static bool OfNullableUnion(ExpressionSyntax expression, SemanticModel model, CancellationToken ct) =>
        Held(expression) is { } held
        && model.GetTypeInfo(held, ct).Type is { } type
        && Symbols.UnderlyingOfNullable((type.IsUnion ? null : UnionElementOf(type)) ?? type) is not null;

    /// <summary>
    /// Whether <paramref name="chain"/> is the name of a union's type, <c>pet.GetType().Name</c>, which is never null, and what it's in may count on that, since it doesn't <see cref="DealsWithNull">deal with null</see>.
    /// The name of the Value's type can be null, so it gets <c>?? ""</c>, the way ToString does.
    /// </summary>
    private static bool NameCountedOn(ExpressionSyntax chain) =>
        chain is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Name", Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax getType } }
        && IsGetType(getType)
        && !DealsWithNull(chain);

    /// <summary>
    /// <c>(text ?? "")</c>, for text that's never null where it's formatted, but can be null once it's the Value's.
    /// </summary>
    private static ExpressionSyntax OrEmpty(ExpressionSyntax text) =>
        ParenthesizedExpression(BinaryExpression(SyntaxKind.CoalesceExpression, text, Token(TriviaList(Space), SyntaxKind.QuestionQuestionToken, TriviaList(Space)), LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(""))))
        .WithAdditionalAnnotations(Simplifier.Annotation);

    /// <summary>
    /// Where UN0015 is reported because a union's type is formatted (<c>pet.GetType().Name</c>, on the union or the whole chain): the chain, which becomes <c>pet.Value?.GetType().Name</c>, since the Value can be null.
    /// That's the whole chain, which the <c>?.</c> then covers, unless what it's in <see cref="NameCountedOn">counts on the name</see>:
    /// then it's just <c>pet.GetType().Name</c>, which becomes <c>(pet.Value?.GetType().Name ?? "")</c>, and what's after it stays as it is.
    /// </summary>
    private static ExpressionSyntax? TypeOfChain(ExpressionSyntax formatted)
    {
        var getType = formatted.Parent as MemberAccessExpressionSyntax is { } onUnion && IsGetType(onUnion)
            ? onUnion
            : formatted.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(IsGetType);

        if (getType?.Parent is not InvocationExpressionSyntax call)
        {
            return null;
        }

        if (call.Parent is MemberAccessExpressionSyntax name && name.Expression == call && NameCountedOn(name))
        {
            return name;
        }

        ExpressionSyntax chain = call;

        while (chain.Parent is MemberAccessExpressionSyntax { Expression: var inner } outer && inner == chain)
        {
            chain = outer;
        }

        return chain;
    }

    private static bool IsGetType(MemberAccessExpressionSyntax access) =>
        access is { Name.Identifier.ValueText: "GetType", Parent: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } };

    /// <summary>
    /// What's formatted, with its Value in place of the union (<paramref name="current"/> is <paramref name="original"/> as it is so far).
    /// In a conditional access, that's the member it gets: <c>order?.Status</c> becomes <c>order?.Status.Value</c>.
    /// </summary>
    private static ExpressionSyntax Replacement(ExpressionSyntax original, ExpressionSyntax current, bool sequence, bool nullable) =>
        TypeOfChain(original) is not null && current.DescendantNodes().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(IsGetType) is { } getType
            ? TypeOfValue(original, current, getType)
            : current is ConditionalAccessExpressionSyntax access
                ? access.WithWhenNotNull(Replacement(((ConditionalAccessExpressionSyntax)original).WhenNotNull, access.WhenNotNull, sequence, nullable))
                : sequence
                    ? InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, CodeFixSyntax.Receiver(current.WithoutTrivia()), IdentifierName("Select")),
                        ArgumentList(SingletonSeparatedList(Argument(SimpleLambdaExpression(Parameter(Identifier("x")), ValueOf(IdentifierName("x"), nullable)))))
                    )
                    : CallsToString(current, out var receiver)
                        ? ValueText(original, receiver, nullable)
                        : ValueOf(current, nullable);

    /// <summary>
    /// The chain a union's type is formatted in (<paramref name="current"/> is <paramref name="original"/> as it is so far), with the type of its Value instead: <c>pet.Value?.GetType().Name</c>,
    /// and <c>?? ""</c> where what it's in <see cref="NameCountedOn">counts on the name</see>.
    /// </summary>
    private static ExpressionSyntax TypeOfValue(ExpressionSyntax original, ExpressionSyntax current, MemberAccessExpressionSyntax getType)
    {
        var typeOfValue = ConditionalAccessExpression(CodeFixSyntax.ValueOf(getType.Expression.WithoutTrivia()), current.ReplaceNode(getType, MemberBindingExpression(getType.Name)));
        return NameCountedOn(original) ? OrEmpty(typeOfValue) : typeOfValue;
    }

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
    /// <c>union.Value?.ToString() ?? ""</c> for <c>union.ToString()</c>, which is never null, and what it's in may count on that, or <c>.Value?.ToString()</c> for <c>?.ToString()</c>, with no <paramref name="union"/>.
    /// Without the <c>?? ""</c> where what it's in <see cref="DealsWithNull">deals with null</see> already.
    /// </summary>
    private static ExpressionSyntax ValueText(ExpressionSyntax toStringCall, ExpressionSyntax? union, bool nullable)
    {
        var text = ConditionalAccessExpression(
            union is null ? MemberBindingExpression(IdentifierName("Value")) : ValueOf(union, nullable),
            InvocationExpression(MemberBindingExpression(IdentifierName("ToString")))
        );

        return DealsWithNull(toStringCall) ? text : OrEmpty(text);
    }

    /// <summary>
    /// The file with a using for LINQ, for a sequence's Select, written out in full rather than left for the IDE to format, so it can be compiled first.
    /// </summary>
    private static SyntaxNode WithUsingForLinq(SyntaxNode root)
    {
        if (root is not CompilationUnitSyntax unit)
        {
            return root;
        }

        var lineBreak = unit.DescendantTrivia().FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

        return unit.AddUsings(
            UsingDirective(ParseName("System.Linq"))
                .WithUsingKeyword(Token(TriviaList(), SyntaxKind.UsingKeyword, TriviaList(Space)))
                .WithTrailingTrivia(lineBreak.IsKind(SyntaxKind.EndOfLineTrivia) ? lineBreak : ElasticCarriageReturnLineFeed)
        );
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
        
        if (root is null || model is null || Formatted(root, context.Span) is not { } expression
            || !await CompilesAsync(context.Document, model, TypeOfChain(expression) ?? expression, context.CancellationToken).ConfigureAwait(false)
        )
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

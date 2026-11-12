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
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using UnionNarrowing.Suppressors.Analysis;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For a union formatted as text (UN0015), or one that records print (UN0019), where the union is declared in the solution:
/// overrides the union's ToString to format what it holds, <c>public override string ToString() =&gt; Value?.ToString() ?? "";</c>.
/// That fixes every UN0015 and UN0019 for the union at once.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OverrideToStringCodeFix)), Shared]
public sealed class OverrideToStringCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.UnionAsText.Id, Descriptors.RecordPrintsUnion.Id];

    /// <summary>
    /// Where to add the override:
    /// the union's declaration, if it's in the solution, and the union has a <c>Value</c> to format and no ToString of its own.
    /// </summary>
    private static async Task<(Document Document, TypeDeclarationSyntax Declaration)?> DeclarationOf(ITypeSymbol union, Solution solution, CancellationToken ct)
    {
        if (!union.GetMembers("Value").OfType<IPropertySymbol>().Any() || union.GetMembers("ToString").OfType<IMethodSymbol>().Any(m => m.Parameters.IsEmpty))
        {
            return null;
        }

        foreach (var reference in union.OriginalDefinition.DeclaringSyntaxReferences)
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } document && await reference.GetSyntaxAsync(ct).ConfigureAwait(false) is TypeDeclarationSyntax declaration)
            {
                return (document, declaration);
            }
        }

        return null;
    }

    /// <summary>
    /// For UN0015, the type of what's formatted:
    /// <c>pet</c>, the receiver of <c>pet.ToString()</c>, or a sequence <c>pets</c>.
    /// </summary>
    private static ITypeSymbol? FormattedType(SyntaxNode root, TextSpan span, SemanticModel model, CancellationToken ct) => FormatValueCodeFix.Formatted(root, span) switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } toString } => model.GetTypeInfo(toString.Expression, ct).Type,
        { } expression => model.GetTypeInfo(expression, ct).Type,
        null => null,
    };

    /// <summary>
    /// For UN0019, the union records print:
    /// where it's declared in source, it's reported on the union itself, and otherwise on the member a record prints.
    /// </summary>
    private static ITypeSymbol? PrintedType(SyntaxNode root, TextSpan span, SemanticModel model, CancellationToken ct) => model.GetDeclaredSymbol(root.FindNode(span), ct) switch
    {
        ITypeSymbol union => union,
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => null,
    };

    /// <summary>
    /// The union a diagnostic is about:
    /// the one formatted, or the elements of a sequence that is (UN0015), or the one a record prints (UN0019).
    /// </summary>
    private static ITypeSymbol? UnionOf(SyntaxNode root, Diagnostic diagnostic, SemanticModel model, CancellationToken ct)
    {
        var type = diagnostic.Id == Descriptors.UnionAsText.Id
            ? FormattedType(root, diagnostic.Location.SourceSpan, model, ct)
            : PrintedType(root, diagnostic.Location.SourceSpan, model, ct);
        type = Symbols.UnderlyingOfNullable(type) ?? type;

        var union = type is { IsUnion: false } ? FormatValueCodeFix.UnionElementOf(type) : type;

        return Symbols.UnderlyingOfNullable(union) ?? union;
    }

    /// <summary>
    /// The declaration with the override added, giving it a body if it has none (<c>union Pet(Cat, Dog);</c>).
    /// </summary>
    private static TypeDeclarationSyntax WithToString(TypeDeclarationSyntax declaration)
    {
        var toString = ParseMemberDeclaration("public override string ToString() => Value?.ToString() ?? \"\";")!.WithAdditionalAnnotations(Formatter.Annotation);
        if (declaration.OpenBraceToken.IsKind(SyntaxKind.None))
        {
            declaration = declaration
                .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken))
                .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(declaration.SemicolonToken.TrailingTrivia))
                .WithSemicolonToken(default)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        return declaration.AddMembers(toString);
    }

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var ct = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var solution = context.Document.Project.Solution;

        if (root is null || model is null
            || UnionOf(root, context.Diagnostics[0], model, ct) is not { } union
            || await DeclarationOf(union, solution, ct).ConfigureAwait(false) is not var (document, declaration)
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Override ToString in '{union.Name}'",
                async ct =>
                {
                    var declaringRoot = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    var newDocument = document.WithSyntaxRoot(declaringRoot!.ReplaceNode(declaration, WithToString(declaration)));
                    return (await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false)).Project.Solution;
                },
                equivalenceKey: nameof(OverrideToStringCodeFix)
            ),
            context.Diagnostics
        );
    }
}

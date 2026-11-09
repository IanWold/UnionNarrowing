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
/// For a settable union member that some constructor leaves holding no case (UN0007):
/// makes it <c>required</c>, so whoever creates the object has to set it.
/// Offered for settable or <c>init</c> properties and fields that aren't readonly, where the member and its setter are at least as visible as their type (which <c>required</c> needs).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddRequiredCodeFix)), Shared]
public sealed class AddRequiredCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.UnassignedUnionMember.Id];

    private static async Task<Document> AddRequiredAsync(Document document, MemberDeclarationSyntax member, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var required = Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(Space);

        // After the other modifiers; with none, it takes over the member's leading trivia.
        var updated = member.Modifiers.Count > 0
            ? member.WithModifiers(member.Modifiers.Add(required))
            : member.WithoutLeadingTrivia().WithModifiers(TokenList(required)).WithLeadingTrivia(member.GetLeadingTrivia());

        return document.WithSyntaxRoot(root!.ReplaceNode(member, updated));
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

        var token = root.FindToken(context.Span.Start);
        MemberDeclarationSyntax? member = token.Parent switch
        {
            PropertyDeclarationSyntax property when property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true => property,
            VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } when !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)) => field,
            _ => null,
        };

        if (member is null
            || token.Parent is null
            || model.GetDeclaredSymbol(token.Parent, ct) is not { IsStatic: false, ContainingType: { } type } symbol
            || symbol.DeclaredAccessibility < type.DeclaredAccessibility
            || symbol is IPropertySymbol { SetMethod: { } setter } && setter.DeclaredAccessibility < type.DeclaredAccessibility
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Make it required", ct => AddRequiredAsync(context.Document, member, ct), equivalenceKey: nameof(AddRequiredCodeFix)),
            context.Diagnostics
        );
    }
}

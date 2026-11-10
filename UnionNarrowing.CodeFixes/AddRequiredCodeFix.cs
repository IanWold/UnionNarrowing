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
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using UnionNarrowing.Suppressors.Analysis;
using UnionNarrowing.Suppressors.Analyzers;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// For a settable union member that some constructor leaves holding no case (UN0007):
/// makes it <c>required</c>, so whoever creates the object has to set it.
/// A constructor that already assigns it gets <c>[SetsRequiredMembers]</c>, so its callers don't have to set it as well;
/// that's only true if it assigns every other required member too, so the fix isn't offered where a constructor that assigns it doesn't.
/// Offered for settable or <c>init</c> properties and fields that aren't readonly, where the member and its setter are at least as visible as their type (which <c>required</c> needs).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddRequiredCodeFix)), Shared]
public sealed class AddRequiredCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [Descriptors.UnassignedUnionMember.Id];

    private static async Task<Document> AddRequiredAsync(Document document, MemberDeclarationSyntax member, IReadOnlyList<ConstructorDeclarationSyntax> setters, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var required = Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(Space);
        var lineBreak = root!.DescendantTrivia().FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

        // After the other modifiers; with none, it takes over the member's leading trivia.
        var updated = member.Modifiers.Count > 0
            ? member.WithModifiers(member.Modifiers.Add(required))
            : member.WithoutLeadingTrivia().WithModifiers(TokenList(required)).WithLeadingTrivia(member.GetLeadingTrivia());

        // On a line of its own before the constructor, which keeps its indentation.
        SyntaxNode SetsRequiredMembers(ConstructorDeclarationSyntax constructor)
        {
            var leading = constructor.GetLeadingTrivia();
            var indentation = leading.Count > 0 && leading.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? TriviaList(leading.Last()) : TriviaList();
            var attribute = AttributeList(SingletonSeparatedList(Attribute(ParseName("System.Diagnostics.CodeAnalysis.SetsRequiredMembers").WithAdditionalAnnotations(Simplifier.Annotation))))
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(lineBreak.IsKind(SyntaxKind.EndOfLineTrivia) ? lineBreak : ElasticCarriageReturnLineFeed);
            var indented = constructor.WithLeadingTrivia(indentation);

            return indented.WithAttributeLists(indented.AttributeLists.Insert(0, attribute));
        }

        var newRoot = root.ReplaceNodes(setters.Append<SyntaxNode>(member), (original, _) => original == member ? updated : SetsRequiredMembers((ConstructorDeclarationSyntax)original));
        return await Simplifier.ReduceAsync(document.WithSyntaxRoot(newRoot), Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The constructors that assign <paramref name="member"/>, themselves or through one they chain to, and don't say they set the required members:
    /// once it's required, they need <c>[SetsRequiredMembers]</c>, or their callers would have to set it as well.
    /// Null where one of them can't say that, because it doesn't assign every other required member (or one is in another file).
    /// </summary>
    private static List<ConstructorDeclarationSyntax>? ConstructorsThatSetIt(ISymbol member, INamedTypeSymbol type, SemanticModel model, CancellationToken ct)
    {
        static bool IsRequired(ISymbol symbol) =>
            symbol is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true };

        static bool SaysItSetsThem(IMethodSymbol constructor) =>
            Symbols.HasAttribute(constructor, "SetsRequiredMembersAttribute", "System", "Diagnostics", "CodeAnalysis");

        var declared = type.InstanceConstructors
            .Select(constructor => (Symbol: constructor, Syntax: constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as ConstructorDeclarationSyntax))
            .Where(constructor => constructor.Syntax is not null)
            .ToList();

        var assignments = new Dictionary<IMethodSymbol, (ImmutableHashSet<ISymbol> Assigned, IMethodSymbol? Chained)>(SymbolEqualityComparer.Default);
        foreach (var (constructor, syntax) in declared)
        {
            if (Functions.SemanticModelFor(syntax!, model).GetOperation(syntax!, ct) is IConstructorBodyOperation body)
            {
                assignments[constructor] = UnassignedUnionMemberAnalyzer.Assignments(ControlFlowGraph.Create(body, ct), type);
            }
        }

        var otherRequired = type.GetMembers().Where(IsRequired).ToList();
        var inheritsRequired = false;
        
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            inheritsRequired |= baseType.GetMembers().Any(IsRequired);
        }

        var setters = new List<ConstructorDeclarationSyntax>();
        foreach (var (constructor, syntax) in declared)
        {
            var assigned = UnassignedUnionMemberAnalyzer.AssignedBy(constructor, assignments);
            if (!assigned.Contains(member) || SaysItSetsThem(constructor))
            {
                continue;
            }

            if (inheritsRequired || !otherRequired.All(assigned.Contains) || syntax!.SyntaxTree != model.SyntaxTree)
            {
                return null;
            }

            setters.Add(syntax);
        }

        return setters;
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

        // `required` has to go on every part of a partial property.
        if (member is null
            || member.Modifiers.Any(SyntaxKind.RequiredKeyword)
            || member.Modifiers.Any(SyntaxKind.PartialKeyword)
            || token.Parent is null
            || model.GetDeclaredSymbol(token.Parent, ct) is not { IsStatic: false, ContainingType: { } type } symbol
            || symbol.DeclaredAccessibility < type.DeclaredAccessibility
            || symbol is IPropertySymbol { SetMethod: { } setter } && setter.DeclaredAccessibility < type.DeclaredAccessibility
            || ConstructorsThatSetIt(symbol, type, model, ct) is not { } setters
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Make it required", ct => AddRequiredAsync(context.Document, member, setters, ct), equivalenceKey: nameof(AddRequiredCodeFix)),
            context.Diagnostics
        );
    }
}

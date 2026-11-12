using System.Collections.Immutable;

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
            var attribute = AttributeList(SingletonSeparatedList(Attribute(CodeFixSyntax.FullName("System.Diagnostics.CodeAnalysis.SetsRequiredMembers"))))
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(lineBreak.IsKind(SyntaxKind.EndOfLineTrivia) ? lineBreak : ElasticCarriageReturnLineFeed);
            var indented = constructor.WithLeadingTrivia(indentation);

            return indented.WithAttributeLists(indented.AttributeLists.Insert(0, attribute));
        }

        var newRoot = root.ReplaceNodes(setters.Append<SyntaxNode>(member), (original, _) => original == member ? updated : SetsRequiredMembers((ConstructorDeclarationSyntax)original));
        return await CodeFixSyntax.ShortenNamesAsync(document.WithSyntaxRoot(newRoot), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether code in the solution would stop compiling once <paramref name="member"/> is required:
    /// a class that derives from <paramref name="type"/>,
    /// a <c>new</c> that doesn't set it with one of the <paramref name="constructors"/> that leave it to their callers,
    /// or <paramref name="type"/> as a type argument for a <c>new()</c> constraint, which a type with required members doesn't meet.
    /// </summary>
    private static async Task<bool> BreaksCodeAsync(Solution solution, ISymbol member, INamedTypeSymbol type, IEnumerable<IMethodSymbol> constructors, CancellationToken ct)
    {
        if ((await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: ct).ConfigureAwait(false)).Any())
        {
            return true;
        }

        foreach (var constructor in constructors)
        {
            foreach (var (node, _) in await ReferencesAsync(constructor, solution, ct).ConfigureAwait(false))
            {
                switch (node.FirstAncestorOrSelf<SyntaxNode>(n => n is BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax))
                {
                    case ConstructorInitializerSyntax:
                        continue;

                    case BaseObjectCreationExpressionSyntax { Initializer: { } initializer }
                        when initializer.Expressions.Any(e => e is AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } && name.Identifier.ValueText == member.Name):
                        continue;

                    default:
                        return true;
                }
            }
        }

        foreach (var (node, model) in await ReferencesAsync(type, solution, ct).ConfigureAwait(false))
        {
            if (node.AncestorsAndSelf().TakeWhile(n => n is TypeSyntax).LastOrDefault() is TypeSyntax { Parent: TypeArgumentListSyntax { Parent: GenericNameSyntax generic } arguments } written
                && model.GetSymbolInfo(generic, ct).Symbol switch { IMethodSymbol method => method.TypeParameters, INamedTypeSymbol named => named.TypeParameters, _ => [] } is var parameters
                && parameters.ElementAtOrDefault(arguments.Arguments.IndexOf(written)) is { HasConstructorConstraint: true }
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The type's constructors that need <c>[SetsRequiredMembers]</c> once <paramref name="member"/> is required, and the ones that leave it to their callers.
    /// The ones that assign it (themselves, or through one they chain to) and don't say they set the required members need the attribute, or their callers would have to set it as well.
    /// Null where one of them can't say that, because it doesn't assign every other required member (or it's in another file).
    /// </summary>
    private static (List<ConstructorDeclarationSyntax> Setters, List<IMethodSymbol> LeavingIt)? Constructors(ISymbol member, INamedTypeSymbol type, SemanticModel model, CancellationToken ct)
    {
        static bool IsRequired(ISymbol symbol) =>
            symbol is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true };

        static bool SaysItSetsThem(IMethodSymbol constructor) =>
            Symbols.HasAttribute(constructor, "SetsRequiredMembersAttribute", "System", "Diagnostics", "CodeAnalysis");

        var declared = new Dictionary<IMethodSymbol, ConstructorDeclarationSyntax>(SymbolEqualityComparer.Default);
        var assignments = new Dictionary<IMethodSymbol, (ImmutableHashSet<ISymbol> Assigned, IMethodSymbol? Chained)>(SymbolEqualityComparer.Default);
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is ConstructorDeclarationSyntax syntax
                && Functions.SemanticModelFor(syntax, model).GetOperation(syntax, ct) is IConstructorBodyOperation body
            )
            {
                declared[constructor] = syntax;
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
        var leavingIt = new List<IMethodSymbol>();
        foreach (var constructor in type.InstanceConstructors.Where(constructor => !SaysItSetsThem(constructor)))
        {
            var assigned = UnassignedUnionMemberAnalyzer.AssignedBy(constructor, assignments);
            if (!assigned.Contains(member))
            {
                leavingIt.Add(constructor);
            }
            else if (inheritsRequired || !otherRequired.All(assigned.Contains) || declared[constructor].SyntaxTree != model.SyntaxTree)
            {
                return null;
            }
            else
            {
                setters.Add(declared[constructor]);
            }
        }

        return (setters, leavingIt);
    }

    /// <summary>
    /// Where the solution refers to <paramref name="symbol"/>, with the semantic model of each place.
    /// </summary>
    private static async Task<List<(SyntaxNode Node, SemanticModel Model)>> ReferencesAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        var references = new List<(SyntaxNode, SemanticModel)>();

        foreach (var location in (await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false)).SelectMany(reference => reference.Locations))
        {
            var root = await location.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var model = await location.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);

            references.Add((root!.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true), model!));
        }

        return references;
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
            || Symbols.HasAttribute(symbol, "ObsoleteAttribute", "System")
            || Constructors(symbol, type, model, ct) is not { } constructors
            || await BreaksCodeAsync(context.Document.Project.Solution, symbol, type, constructors.LeavingIt, ct).ConfigureAwait(false)
        )
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Make it required", ct => AddRequiredAsync(context.Document, member, constructors.Setters, ct), equivalenceKey: nameof(AddRequiredCodeFix)),
            context.Diagnostics
        );
    }
}

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Syntax the code fixes write.
/// </summary>
internal static class CodeFixSyntax
{
    /// <summary>
    /// Whether the document compiles as well as it did with <paramref name="original"/> replaced by <paramref name="replacement"/>, and whatever else <paramref name="alsoChange"/> changes (a using it needs, say):
    /// no more errors in the member it's in than there were.
    /// What a fix writes can be fine syntax and still not compile where it goes:
    /// <c>pet.Value</c> passed where only a <c>Pet</c> will do, or <c>?.</c> in a lambda that's an expression tree.
    /// What's compiled is the text parsed again, as the IDE would, not the tree it's printed from.
    /// </summary>
    public static async Task<bool> CompilesAsync(Document document, SyntaxNode original, SyntaxNode replacement, CancellationToken ct, Func<SyntaxNode, SyntaxNode>? alsoChange = null)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return false;
        }

        var written = new SyntaxAnnotation();
        var rewritten = root.ReplaceNode(original, replacement.WithAdditionalAnnotations(written));
        
        rewritten = alsoChange?.Invoke(rewritten) ?? rewritten;

        var rewrittenModel = await document.WithText(rewritten.GetText()).GetSemanticModelAsync(ct).ConfigureAwait(false);
        
        return rewrittenModel is not null
            && rewritten.GetAnnotatedNodes(written).FirstOrDefault() is { } node
            && Errors(rewrittenModel, MemberOf(node), ct) <= Errors(model, MemberOf(original), ct);
    }

    private static int Errors(SemanticModel model, SyntaxNode member, CancellationToken ct) =>
        model.GetDiagnostics(member.Span, ct).Count(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// A type's full name, which <see cref="ShortenNamesAsync"/> shortens.
    /// </summary>
    public static NameSyntax FullName(string fullName) =>
        ParseName(fullName).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

    /// <summary>
    /// The whitespace the line starts with, for what has <paramref name="leading"/> as its leading trivia.
    /// </summary>
    public static SyntaxTriviaList Indentation(SyntaxTriviaList leading)
    {
        var lineStart = leading.Count - leading.Reverse().TakeWhile(t => !t.IsKind(SyntaxKind.EndOfLineTrivia) && !t.IsDirective).Count();
        return TriviaList(leading.Skip(lineStart).TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)));
    }

    /// <summary>
    /// Whether the expression is part of a conditional access, after its <c>?</c>, which it can't be taken out of:
    /// <c>.Pet.ToString()</c> in <c>owner?.Pet.ToString()</c>.
    /// What it's in can be null anyway.
    /// </summary>
    public static bool InConditionalAccess(ExpressionSyntax expression) =>
        expression.GetFirstToken().Parent is MemberBindingExpressionSyntax or BracketedArgumentListSyntax { Parent: ElementBindingExpressionSyntax };

    /// <summary>
    /// The member (or top-level statement) a node is in, or the whole file.
    /// </summary>
    private static SyntaxNode MemberOf(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(n => n is MemberDeclarationSyntax) ?? node.AncestorsAndSelf().Last();

    /// <summary>
    /// <c>new X()</c> for an exception type given by its <see cref="FullName">full name</see>.
    /// </summary>
    public static ObjectCreationExpressionSyntax NewException(string fullName) =>
        ObjectCreationExpression(FullName(fullName)).WithArgumentList(ArgumentList());

    /// <summary>
    /// The expression, in parentheses unless it can take a member access as it is.
    /// </summary>
    public static ExpressionSyntax Receiver(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax
            or MemberBindingExpressionSyntax or ElementBindingExpressionSyntax
            ? expression
            : ParenthesizedExpression(expression);

    /// <summary>
    /// Adds a using for each namespace the names written in full (<see cref="FullName"/>) are in, where the file doesn't have one, and then shortens the names.
    /// A name whose short form would mean something else where it's written (a type of that name in an enclosing namespace, say) gets no using, and keeps as much of its namespace as it needs.
    /// </summary>
    public static async Task<Document> ShortenNamesAsync(Document document, CancellationToken ct)
    {
        var root = (await document.GetSyntaxRootAsync(ct).ConfigureAwait(false))!;
        var model = (await document.GetSemanticModelAsync(ct).ConfigureAwait(false))!;
        var clashing = root.GetAnnotatedNodes(Simplifier.AddImportsAnnotation).OfType<NameSyntax>()
            .Where(name => model.GetTypeInfo(name, ct).Type is { } type
                && model.LookupNamespacesAndTypes(name.SpanStart, name: type.Name).Any(other => !SymbolEqualityComparer.Default.Equals(other, type))
            );

        document = document.WithSyntaxRoot(root.ReplaceNodes(clashing, (_, name) => name.WithoutAnnotations(Simplifier.AddImportsAnnotation)));
        document = await ImportAdder.AddImportsAsync(document, Simplifier.AddImportsAnnotation, cancellationToken: ct).ConfigureAwait(false);

        return await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>union.Value</c>.
    /// </summary>
    public static MemberAccessExpressionSyntax ValueOf(ExpressionSyntax union) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Receiver(union.WithoutTrivia()), IdentifierName("Value"));
}

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Syntax the code fixes write.
/// </summary>
internal static class CodeFixSyntax
{
    /// <summary>
    /// <c>new X()</c> for an exception type given by its full name, which <see cref="ShortenNamesAsync"/> shortens.
    /// </summary>
    public static ObjectCreationExpressionSyntax NewException(string fullName) =>
        ObjectCreationExpression(ParseTypeName(fullName).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation)).WithArgumentList(ArgumentList());

    /// <summary>
    /// The expression, in parentheses unless it can take a member access as it is.
    /// </summary>
    public static ExpressionSyntax Receiver(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax
            ? expression
            : ParenthesizedExpression(expression);

    /// <summary>
    /// Adds a using for each namespace the names written in full (<see cref="NewException"/>) are in, where the file doesn't have one, and then shortens the names.
    /// </summary>
    public static async Task<Document> ShortenNamesAsync(Document document, CancellationToken ct)
    {
        document = await ImportAdder.AddImportsAsync(document, Simplifier.AddImportsAnnotation, cancellationToken: ct).ConfigureAwait(false);
        return await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>union.Value</c>.
    /// </summary>
    public static MemberAccessExpressionSyntax ValueOf(ExpressionSyntax union) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Receiver(union.WithoutTrivia()), IdentifierName("Value"));
}

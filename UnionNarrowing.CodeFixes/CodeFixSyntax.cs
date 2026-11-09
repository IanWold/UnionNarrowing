using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// Syntax the code fixes write.
/// </summary>
internal static class CodeFixSyntax
{
    /// <summary>
    /// <c>new X()</c> for an exception type given by its full name, which the simplifier shortens.
    /// </summary>
    public static ObjectCreationExpressionSyntax NewException(string fullName) =>
        ObjectCreationExpression(ParseTypeName(fullName).WithAdditionalAnnotations(Simplifier.Annotation)).WithArgumentList(ArgumentList());

    /// <summary>
    /// The expression, in parentheses unless it can take a member access as it is.
    /// </summary>
    public static ExpressionSyntax Receiver(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax
            ? expression
            : ParenthesizedExpression(expression);

    /// <summary>
    /// <c>union.Value</c>.
    /// </summary>
    public static MemberAccessExpressionSyntax ValueOf(ExpressionSyntax union) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Receiver(union.WithoutTrivia()), IdentifierName("Value"));
}

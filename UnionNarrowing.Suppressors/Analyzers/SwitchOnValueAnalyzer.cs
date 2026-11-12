using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports a switch on a union's <c>Value</c> that could switch on the union itself.
/// Patterns on a union test its Value, so the switch matches the same way, and the compiler can then check it against the union's case types.
/// </summary>
/// <remarks>
/// Only reported when every pattern means the same thing either way.
/// A <c>var</c> pattern, or a property pattern without a type (<c>{ }</c>), applies to the union itself rather than to its Value.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwitchOnValueAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.SwitchOnValue];

    /// <summary>
    /// Whether the compiler accepts a type test against <paramref name="type"/> on the union, meaning the same as on its Value:
    /// some case type could be one.
    /// Not a type parameter, which could be the union itself (<c>static bool Is&lt;T&gt;(this Pet pet) =&gt; pet.Value is T;</c>).
    /// </summary>
    private static bool Allowed(ITypeSymbol type, ImmutableArray<ITypeSymbol> caseTypes, CSharpCompilation compilation) =>
        !type.IsUnion && type is not ITypeParameterSymbol && caseTypes.Any(caseType =>
            caseType is ITypeParameterSymbol
            || compilation.ClassifyConversion(caseType, type) is { IsIdentity: true } or { IsReference: true } or { IsBoxing: true } or { IsUnboxing: true }
        );

    /// <summary>
    /// For the message about a test of a union's Value: what to do instead, and what the compiler then checks.
    /// </summary>
    private static readonly (string Instead, string Checks) Test = ("Test", "that it's one of the union's cases");

    /// <param name="message">What to do instead, and what the compiler then checks.</param>
    private static void Analyze(OperationAnalysisContext context, IOperation input, IEnumerable<IPatternOperation> patterns, (string Instead, string Checks) message)
    {
        if (ValueOfUnion(input) is not var (unionType, access) || context.Operation.SemanticModel is not { } model)
        {
            return;
        }

        var compilation = (CSharpCompilation)model.Compilation;
        if (patterns.All(pattern => MeansTheSameOnTheUnion(pattern, unionType.UnionCaseTypes, compilation)))
        {
            Report(context, access, message);
        }
    }

    private static void AnalyzeSwitchExpression(OperationAnalysisContext context)
    {
        var switchOp = (ISwitchExpressionOperation)context.Operation;
        Analyze(context, switchOp.Value, switchOp.Arms.Select(arm => arm.Pattern), ("Switch on", "that every case is handled"));
    }

    private static void AnalyzeSwitchStatement(OperationAnalysisContext context)
    {
        var switchOp = (ISwitchOperation)context.Operation;
        var clauses = switchOp.Cases.SelectMany(c => c.Clauses).ToList();

        // Constant and default labels mean the same either way. Anything else (a relational label, say) isn't worth
        // reasoning about.
        if (clauses.Any(c => c is not (IPatternCaseClauseOperation or ISingleValueCaseClauseOperation or IDefaultCaseClauseOperation)))
        {
            return;
        }

        Analyze(context, switchOp.Value, clauses.OfType<IPatternCaseClauseOperation>().Select(c => c.Pattern), ("Switch on", "that each label is for one of the union's cases"));
    }

    /// <summary>
    /// <c>pet.Value is Cat c</c>, which could be <c>pet is Cat c</c>.
    /// </summary>
    private static void AnalyzeTest(OperationAnalysisContext context)
    {
        switch (context.Operation)
        {
            case IIsPatternOperation isPattern:
                Analyze(context, isPattern.Value, [isPattern.Pattern], Test);
                break;

            // `x is T` with a type, which isn't a pattern: what matters is whether the union allows the type.
            case IIsTypeOperation isType when ValueOfUnion(isType.ValueOperand) is var (union, access)
                && context.Operation.SemanticModel is { } model
                && Allowed(isType.TypeOperand, union.UnionCaseTypes, (CSharpCompilation)model.Compilation):
                Report(context, access, Test);
                break;
        }
    }

    /// <summary>
    /// Whether the pattern tests a union's Value the same way it tests the Value directly, and is allowed on the union:
    /// the compiler only accepts types and constants a case type could match.
    /// </summary>
    private static bool MeansTheSameOnTheUnion(IPatternOperation pattern, ImmutableArray<ITypeSymbol> caseTypes, CSharpCompilation compilation)
    {
        return pattern switch
        {
            IDeclarationPatternOperation { MatchesNull: true } => false, // `var x` binds the union, not its Value
            IDiscardPatternOperation => true,
            IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } => true,
            ITypePatternOperation typePattern => Allowed(typePattern.MatchedType),
            IDeclarationPatternOperation { MatchedType: { } type } => Allowed(type),
            IRecursivePatternOperation { Syntax: RecursivePatternSyntax { Type: not null } } recursive => Allowed(recursive.MatchedType), // subpatterns test members
            INegatedPatternOperation not => MeansTheSameOnTheUnion(not.Pattern, caseTypes, compilation),
            IBinaryPatternOperation binary =>
                MeansTheSameOnTheUnion(binary.LeftPattern, caseTypes, compilation) && MeansTheSameOnTheUnion(binary.RightPattern, caseTypes, compilation),
            _ => false,
        };

        bool Allowed(ITypeSymbol type) =>
            SwitchOnValueAnalyzer.Allowed(type, caseTypes, compilation);
    }

    /// <summary>
    /// Reports <paramref name="access"/>, a union's <c>x.Value</c>, with what to do instead and what the compiler then checks.
    /// </summary>
    private static void Report(OperationAnalysisContext context, MemberAccessExpressionSyntax access, (string Instead, string Checks) message) =>
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SwitchOnValue, access.GetLocation(), access.Expression.ToString(), message.Instead, message.Checks));

    /// <summary>
    /// For a struct union's <c>Value</c>, the union type and the <c>x.Value</c> syntax.
    /// (A class union could be null, which <c>.Value</c> throws on and a pattern on the union doesn't.)
    /// </summary>
    private static (ITypeSymbol Union, MemberAccessExpressionSyntax Access)? ValueOfUnion(IOperation operation) =>
        operation is IPropertyReferenceOperation
        {
            Property.Name: "Value",
            Instance.Type: { IsUnion: true, IsValueType: true } unionType,
            Syntax: MemberAccessExpressionSyntax access,
        }
            ? (unionType, access)
            : null;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeSwitchExpression, OperationKind.SwitchExpression);
        context.RegisterOperationAction(AnalyzeSwitchStatement, OperationKind.Switch);
        context.RegisterOperationAction(AnalyzeTest, OperationKind.IsPattern, OperationKind.IsType);
    }
}

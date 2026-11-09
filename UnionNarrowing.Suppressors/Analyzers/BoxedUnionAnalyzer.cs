using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports code that treats a boxed union as what it holds.
/// A union is a struct:
/// boxing it boxes the union, whose run-time type is the union's, so:
/// <list type="bullet">
/// <item>UN0014: a type test against a case (<c>(object)pet is Cat</c>) is never true, a cast to one throws (and <c>as</c> gives null), <c>pet.Equals(cat)</c> is false, and <c>pet.GetType() == typeof(Cat)</c> is false.</item>
/// <item>UN0015: formatting a union as text gives its type name, since it doesn't override ToString.</item>
/// <item>UN0019: a record's generated ToString does the same for each of its public union members.</item>
/// </list>
/// </summary>
/// <remarks>
/// <c>OfType&lt;Cat&gt;()</c> and <c>Cast&lt;Cat&gt;()</c> on a sequence of unions are CA2021's.
/// A boxed union in a variable of type object, or a generic T, isn't followed.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BoxedUnionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.BoxedUnionTest, Descriptors.UnionAsText, Descriptors.RecordPrintsUnion];

    /// <summary>
    /// <c>pet.GetType() == typeof(Cat)</c> is never true, since a union's type is the union;
    /// and <c>"Pet: " + pet</c> formats the union.
    /// </summary>
    private static void AnalyzeBinary(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary is { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String })
        {
            foreach (var operand in new[] { binary.LeftOperand, binary.RightOperand })
            {
                if (BoxedUnion(operand) is { } boxed && !OverridesToString(boxed))
                {
                    ReportText(context, operand, ((IConversionOperation)operand).Operand, boxed);
                }
            }

            return;
        }

        if (binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
        {
            return;
        }

        foreach (var (left, right) in new[] { (binary.LeftOperand, binary.RightOperand), (binary.RightOperand, binary.LeftOperand) })
        {
            if (left is IInvocationOperation { TargetMethod.Name: "GetType", Instance.Type: { IsUnion: true, IsValueType: true } union }
                && right is ITypeOfOperation { TypeOperand: var tested }
                && !SymbolEqualityComparer.Default.Equals(tested, union)
            )
            {
                var name = Name(union, binary);

                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, binary.Syntax.GetLocation(), binary.Syntax.ToString(),
                    $"is {(binary.OperatorKind == BinaryOperatorKind.Equals ? "never" : "always")} true: GetType() on a {name} is always typeof({name}). Test the union itself, which tests what it holds."
                ));

                return;
            }
        }
    }

    /// <summary>
    /// <c>(Cat)(object)pet</c> throws, and <c>(object)pet as Cat</c> is null.
    /// </summary>
    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.IsImplicit
            || conversion.Type is not { } target
            || BoxedUnion(conversion.Operand) is not { } union
            || BoxCanBe(union, target, context.Compilation)
        )
        {
            return;
        }

        var name = Name(union, conversion);
        var what = conversion.IsTryCast
            ? $"is always null: a boxed {name} is a {name}, not one of its cases. Use a pattern on the union instead."
            : $"always throws: a boxed {name} is a {name}, not one of its cases. Use a pattern on the union, or cast its Value.";

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, conversion.Syntax.GetLocation(), conversion.Syntax.ToString(), what));
    }

    /// <summary>
    /// <c>$"{pet}"</c>
    /// </summary>
    private static void AnalyzeInterpolation(OperationAnalysisContext context)
    {
        var interpolation = (IInterpolationOperation)context.Operation;
        var expression = interpolation.Expression is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : interpolation.Expression;
        
        if (expression.Type is { IsUnion: true, IsValueType: true } union && !OverridesToString(union))
        {
            ReportText(context, expression, expression, union);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        // `pet.Equals(cat)`: a union only ever equals another of the same union.
        if (method is { Name: "Equals", Parameters.Length: 1, IsStatic: false }
            && method.Parameters[0].Type.SpecialType == SpecialType.System_Object
            && invocation.Instance?.Type is { IsUnion: true, IsValueType: true } union
            && invocation.Arguments[0].Value is IConversionOperation { Operand.Type: { } argumentType }
            && !argumentType.IsUnion
            && argumentType.SpecialType != SpecialType.System_Object
            && !BoxCanBe(union, argumentType, context.Compilation)
            && !SymbolEqualityComparer.Default.Equals(argumentType, union)
        )
        {
            var name = Name(union, invocation);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, invocation.Syntax.GetLocation(), invocation.Syntax.ToString(),
                $"is never true: a {name} only ever equals another {name}. Compare with a {name} made from it, or compare its Value."
            ));
            return;
        }

        // `ReferenceEquals(pet, other)`: each argument is a new box.
        if (method is { Name: "ReferenceEquals", IsStatic: true, Parameters.Length: 2, ContainingType.SpecialType: SpecialType.System_Object }
            && invocation.Arguments.Select(a => BoxedUnion(a.Value)).FirstOrDefault(u => u is not null) is { } boxedArgument
        )
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, invocation.Syntax.GetLocation(), invocation.Syntax.ToString(),
                $"is never true: a {Name(boxedArgument, invocation)} is boxed afresh for the call. Compare with Equals instead."
            ));

            return;
        }

        // `pet.ToString()`
        if (method is { Name: "ToString", Parameters.IsEmpty: true }
            && invocation.Instance is { Type: { IsUnion: true, IsValueType: true } formatted } instance
            && !OverridesToString(formatted)
        )
        {
            ReportText(context, invocation, instance, formatted);
            return;
        }

        // `string.Join(", ", pets)`, `builder.AppendJoin(", ", pets)`: each element's ToString.
        if (FormatsItsArguments(method) && method.IsGenericMethod && method.TypeArguments[0] is { IsUnion: true, IsValueType: true } elementType && !OverridesToString(elementType))
        {
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.OriginalDefinition.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T })
                {
                    var sequence = argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;

                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, sequence.Syntax.GetLocation(), sequence.Syntax.ToString(), Name(elementType, invocation),
                        $"'{sequence.Syntax}.Select(x => x.Value)'"
                    ));
                }
            }

            return;
        }

        // `Console.WriteLine(pet)`, `builder.Append(pet)`, `string.Format("{0}", pet)`, `logger.LogInformation("{Pet}", pet)`, ...
        if (FormatsItsArguments(method))
        {
            foreach (var argument in invocation.Arguments)
            {
                foreach (var value in argument.Value is IArrayCreationOperation { IsImplicit: true, Initializer: { } parameters } ? parameters.ElementValues : [argument.Value])
                {
                    if (BoxedUnion(value) is { } boxed && !OverridesToString(boxed))
                    {
                        ReportText(context, value, ((IConversionOperation)value).Operand, boxed);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A record whose generated ToString prints a union member:
    /// it shows the union's type name.
    /// </summary>
    private static void AnalyzeRecord(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.IsRecord
            || type.GetMembers("PrintMembers").Any(m => !m.IsImplicitlyDeclared)
            || type.GetMembers("ToString").Any(m => m is IMethodSymbol { Parameters.IsEmpty: true, IsImplicitlyDeclared: false })
        )
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            var (memberType, printed) = member switch
            {
                IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null, IsIndexer: false } property => (property.Type, true),
                IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false } field => (field.Type, true),
                _ => (null, false),
            };

            var union = Symbols.UnderlyingOfNullable(memberType) ?? memberType;
            if (!printed || union is not { IsUnion: true, IsValueType: true } || OverridesToString(union) || member.Locations.IsEmpty || !member.Locations[0].IsInSource)
            {
                continue;
            }

            var name = union.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.RecordPrintsUnion, member.Locations[0], type.Name, member.Name, name));
        }
    }

    /// <summary>
    /// <c>((object)pet) switch { Cat =&gt; ... }</c>:
    /// an arm (or label) testing for a case never matches.
    /// </summary>
    private static void AnalyzeSwitch(OperationAnalysisContext context)
    {
        var (input, patterns) = context.Operation switch
        {
            ISwitchExpressionOperation expression => (expression.Value, expression.Arms.Select(arm => arm.Pattern)),
            ISwitchOperation statement => (statement.Value, statement.Cases.SelectMany(c => c.Clauses).OfType<IPatternCaseClauseOperation>().Select(c => c.Pattern)),
            _ => (null!, []),
        };

        if (BoxedUnion(input) is not { } union)
        {
            return;
        }

        var name = Name(union, context.Operation);
        foreach (var pattern in patterns.Where(p => NeverMatches(p, union, context.Compilation)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, pattern.Syntax.GetLocation(), pattern.Syntax.ToString(),
                $"never matches: a boxed {name} is a {name}, not one of its cases. Switch on the union itself, without boxing it."
            ));
        }
    }

    private static void AnalyzeTypeTest(OperationAnalysisContext context)
    {
        var (operand, impossible) = context.Operation switch
        {
            IIsTypeOperation isType => (isType.ValueOperand, !isType.IsNegated ? BoxedUnion(isType.ValueOperand) is { } u && !BoxCanBe(u, isType.TypeOperand, context.Compilation) : null),
            IIsPatternOperation isPattern => (isPattern.Value, BoxedUnion(isPattern.Value) is { } u ? (bool?)NeverMatches(isPattern.Pattern, u, context.Compilation) : null),
            _ => (null!, null),
        };

        if (impossible == true && BoxedUnion(operand) is { } union)
        {
            var name = Name(union, context.Operation);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, context.Operation.Syntax.GetLocation(), context.Operation.Syntax.ToString(),
                $"is never true: a boxed {name} is a {name}, not one of its cases. Test the union itself, without boxing it."
            ));
        }
    }

    /// <summary>
    /// Whether a boxed <paramref name="union"/> can be a <paramref name="type"/>:
    /// object, ValueType, the union itself, or an interface it implements.
    /// </summary>
    private static bool BoxCanBe(ITypeSymbol union, ITypeSymbol type, Compilation compilation)
    {
        if (type is ITypeParameterSymbol || type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        var target = Symbols.UnderlyingOfNullable(type) ?? type;
        var conversion = ((CSharpCompilation)compilation).ClassifyConversion(union, target);

        return conversion.IsIdentity || conversion.IsBoxing;
    }

    /// <summary>
    /// For an operation that boxes a struct union (to object, ValueType or an interface), the union's type.
    /// </summary>
    private static ITypeSymbol? BoxedUnion(IOperation operation) =>
        operation is IConversionOperation { Operand.Type: { IsUnion: true, IsValueType: true } union } conversion
        && conversion.GetConversion().IsBoxing
            ? union
            : null;

    /// <summary>
    /// The common ways of turning arguments into text:
    /// Console, TextWriter, StringBuilder, string.Format, string.Concat, string.Join, Debug and Trace.
    /// </summary>
    private static bool FormatsItsArguments(IMethodSymbol method)
    {
        var type = method.ContainingType;
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (Symbols.Is(t, "TextWriter", 0, "System", "IO"))
            {
                return method.Name is "Write" or "WriteLine";
            }
        }

        return (type.ContainingNamespace?.ToDisplayString(), type.Name, method.Name) switch
        {
            ("System", "Console", "Write" or "WriteLine") => true,
            ("System.Text", "StringBuilder", "Append" or "AppendLine" or "Insert" or "AppendFormat" or "AppendJoin") => true,
            ("Microsoft.Extensions.Logging", "LoggerExtensions", "Log" or "LogTrace" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError" or "LogCritical") => true,
            ("System", "String", "Format" or "Concat" or "Join") => true,
            ("System.Diagnostics", "Debug" or "Trace", "Write" or "WriteLine" or "Print" or "WriteIf" or "WriteLineIf") => true,
            _ => false,
        };
    }

    private static string Name(ITypeSymbol type, IOperation operation) =>
        type.ToMinimalDisplayString(operation.SemanticModel!, operation.Syntax.SpanStart);

    /// <summary>
    /// Whether a pattern can't match a boxed union:
    /// it's made of type tests the box can't pass, combined so that one of them has to pass.
    /// </summary>
    private static bool NeverMatches(IPatternOperation pattern, ITypeSymbol union, Compilation compilation) =>
        pattern switch
        {
            ITypePatternOperation type => !BoxCanBe(union, type.MatchedType, compilation),
            IDeclarationPatternOperation { MatchesNull: false, MatchedType: { } type } => !BoxCanBe(union, type, compilation),
            IRecursivePatternOperation { MatchedType: var type } recursive when recursive.Syntax is RecursivePatternSyntax { Type: not null } =>
                !BoxCanBe(union, type, compilation),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => NeverMatches(or.LeftPattern, union, compilation) && NeverMatches(or.RightPattern, union, compilation),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => NeverMatches(and.LeftPattern, union, compilation) || NeverMatches(and.RightPattern, union, compilation),
            _ => false,
        };

    private static bool OverridesToString(ITypeSymbol union) =>
        union.GetMembers("ToString").OfType<IMethodSymbol>().Any(m => m is { IsOverride: true, Parameters.IsEmpty: true });

    private static void ReportText(OperationAnalysisContext context, IOperation reported, IOperation union, ITypeSymbol unionType) =>
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, reported.Syntax.GetLocation(), reported.Syntax.ToString(), Name(unionType, reported), $"'{union.Syntax}.Value'"));

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeTypeTest, OperationKind.IsType, OperationKind.IsPattern);
        context.RegisterOperationAction(AnalyzeSwitch, OperationKind.SwitchExpression, OperationKind.Switch);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeBinary, OperationKind.Binary);
        context.RegisterOperationAction(AnalyzeInterpolation, OperationKind.Interpolation);
        context.RegisterSymbolAction(AnalyzeRecord, SymbolKind.NamedType);
    }
}

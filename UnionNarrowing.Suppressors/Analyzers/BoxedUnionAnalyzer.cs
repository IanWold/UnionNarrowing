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
/// <item>UN0014: a type test against a case (<c>(object)pet is Cat</c>) is never true, a cast to one throws (and <c>as</c> gives null), <c>pet.Equals(cat)</c> (or <c>Equals(pet, cat)</c>) is false, and <c>pet.GetType() == typeof(Cat)</c> is false.</item>
/// <item>UN0015: formatting a union (or a nullable one) as text gives its type name, unless it overrides ToString.</item>
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
            ReportText(context, binary.LeftOperand, binary.LeftOperand);
            ReportText(context, binary.RightOperand, binary.RightOperand);
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
            ? $"is always null: a boxed {name} is a {name}, not what it holds. Use a pattern on the union instead."
            : $"always throws: a boxed {name} is a {name}, not what it holds. Use a pattern on the union, or cast its Value.";

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, conversion.Syntax.GetLocation(), conversion.Syntax.ToString(), what));
    }

    /// <summary>
    /// <c>$"{pet}"</c>, as a string, or through an interpolated string handler (<c>builder.Append($"{pet}")</c>), where each part is a call to the handler's <c>AppendFormatted</c> that takes any value.
    /// </summary>
    private static void AnalyzeInterpolation(OperationAnalysisContext context)
    {
        var formatted = context.Operation switch
        {
            IInterpolationOperation interpolation => interpolation.Expression,
            IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation { Arguments: { Length: > 0 } arguments } }
                when arguments[0].Parameter?.OriginalDefinition.Type is ITypeParameterSymbol or { SpecialType: SpecialType.System_Object } => arguments[0].Value,
            _ => null,
        };

        if (formatted is not null)
        {
            ReportText(context, formatted, formatted);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        // `pet.Equals(cat)`, `Equals(pet, cat)`, `EqualityComparer<object>.Default.Equals(pet, cat)`: a union only ever
        // equals another of the same union.
        if (method.Name == "Equals" && ComparedForEquality(invocation) is var (union, other) && !BoxCanEqual(union, other, context.Compilation))
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
        if (method is { Name: "ToString", Parameters.IsEmpty: true } && invocation.Instance is { } instance)
        {
            ReportText(context, invocation, instance);
            return;
        }

        // `string.Join(", ", pets)`, `builder.AppendJoin(", ", pets)`: each element's ToString.
        if (FormatsItsArguments(method) && method.IsGenericMethod && ShowsTypeName(method.TypeArguments[0]) is { } elementType)
        {
            var held = Symbols.UnderlyingOfNullable(method.TypeArguments[0]) is null ? "x.Value" : "x?.Value";
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.OriginalDefinition.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T })
                {
                    var sequence = argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;

                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, sequence.Syntax.GetLocation(), sequence.Syntax.ToString(), Name(elementType, invocation),
                        Remedy($"format '{sequence.Syntax}.Select(x => {held})' instead", elementType)
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
                    ReportText(context, value, value);
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

            if (!printed || ShowsTypeName(memberType) is not { } union || member.Locations.IsEmpty || !member.Locations[0].IsInSource)
            {
                continue;
            }

            var name = union.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.RecordPrintsUnion, member.Locations[0], type.Name, member.Name, name, Remedy("declare PrintMembers to print its Value", union)));
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
                $"never matches: a boxed {name} is a {name}, not what it holds. Switch on the union itself, without boxing it."
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
                $"is never true: a boxed {name} is a {name}, not what it holds. Test the union itself, without boxing it."
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
    /// Whether a boxed <paramref name="union"/> can equal <paramref name="other"/>, a value it's compared with as an object:
    /// unless that's of a type that's neither a union nor object, and that a boxed union can't be, it might.
    /// </summary>
    private static bool BoxCanEqual(ITypeSymbol union, IOperation other, Compilation compilation) =>
        other is not IConversionOperation { Operand.Type: { IsUnion: false, SpecialType: not SpecialType.System_Object } type }
        || BoxCanBe(union, type, compilation);

    /// <summary>
    /// For an operation that boxes a struct union (to object, ValueType or an interface), the union's type.
    /// </summary>
    private static ITypeSymbol? BoxedUnion(IOperation operation) =>
        operation is IConversionOperation { Operand.Type: { IsUnion: true, IsValueType: true } union } conversion
        && conversion.GetConversion().IsBoxing
            ? union
            : null;

    /// <summary>
    /// For an <c>Equals</c> call that compares a struct union with another value as objects, which uses the union's own Equals:
    /// <c>pet.Equals(x)</c>, the static <c>object.Equals(pet, x)</c>, or <c>EqualityComparer&lt;object&gt;.Default.Equals(pet, x)</c> (either way round).
    /// The union's type, and the other value.
    /// </summary>
    private static (ITypeSymbol Union, IOperation Other)? ComparedForEquality(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method is { IsStatic: false, Parameters.Length: 1 } && method.Parameters[0].Type.SpecialType == SpecialType.System_Object)
        {
            return invocation.Instance?.Type is { IsUnion: true, IsValueType: true } union ? (union, invocation.Arguments[0].Value) : null;
        }

        var objectEquals = method is { IsStatic: true, ContainingType.SpecialType: SpecialType.System_Object };
        var defaultComparer = invocation.Instance is IPropertyReferenceOperation { Property.Name: "Default", Type: var comparer }
            && Symbols.Is(comparer, "EqualityComparer", 1, "System", "Collections", "Generic");

        if (!(objectEquals || defaultComparer) || method.Parameters.Length != 2 || !method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Object))
        {
            return null;
        }

        var (left, right) = (invocation.Arguments[0].Value, invocation.Arguments[1].Value);
        return BoxedUnion(left) is { } leftUnion
            ? (leftUnion, right)
            : BoxedUnion(right) is { } rightUnion
                ? (rightUnion, left)
                : null;
    }

    /// <summary>
    /// The common ways of turning arguments into text:
    /// Console, TextWriter, StringBuilder, string.Format, string.Concat, string.Join, Convert.ToString, Debug and Trace, and a logger's messages and scopes.
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
            ("Microsoft.Extensions.Logging", "LoggerExtensions", "Log" or "LogTrace" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError" or "LogCritical" or "BeginScope") => true,
            ("System", "String", "Format" or "Concat" or "Join") => true,
            ("System", "Convert", "ToString") => true,
            ("System.Diagnostics", "Debug" or "Trace", "Write" or "WriteLine" or "Print" or "WriteIf" or "WriteLineIf" or "TraceInformation" or "TraceWarning" or "TraceError") => true,
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

    /// <summary>
    /// What to do about a union that shows its type name:
    /// <paramref name="here"/>, or, for a union declared in this compilation, override its ToString, which fixes it everywhere.
    /// </summary>
    private static string Remedy(string here, ITypeSymbol union) =>
        union.DeclaringSyntaxReferences.IsEmpty ? here : $"{here}, or override ToString in {union.Name}";

    /// <summary>
    /// Reports <paramref name="formatted"/>, a value turned into text (as it is, or boxed on the way), if it's a union that shows its type name.
    /// </summary>
    /// <param name="reported">Where to report it: the value, or a call to its ToString.</param>
    private static void ReportText(OperationAnalysisContext context, IOperation reported, IOperation formatted)
    {
        var value = formatted is IConversionOperation { IsImplicit: true } conversion && conversion.GetConversion().IsBoxing ? conversion.Operand : formatted;
        if (ShowsTypeName(value.Type) is not { } union)
        {
            return;
        }

        var held = Symbols.UnderlyingOfNullable(value.Type) is null ? ".Value" : "?.Value";
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, reported.Syntax.GetLocation(), reported.Syntax.ToString(), Name(union, reported),
            Remedy($"format '{value.Syntax}{held}' instead", union)
        ));
    }

    /// <summary>
    /// The struct union a value of <paramref name="type"/> is (or holds, as a nullable), if formatting it gives the union's type name:
    /// the union doesn't override ToString.
    /// </summary>
    private static ITypeSymbol? ShowsTypeName(ITypeSymbol? type) =>
        (Symbols.UnderlyingOfNullable(type) ?? type) is { IsUnion: true, IsValueType: true } union
        && !union.GetMembers("ToString").OfType<IMethodSymbol>().Any(m => m is { IsOverride: true, Parameters.IsEmpty: true })
            ? union
            : null;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeTypeTest, OperationKind.IsType, OperationKind.IsPattern);
        context.RegisterOperationAction(AnalyzeSwitch, OperationKind.SwitchExpression, OperationKind.Switch);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeBinary, OperationKind.Binary);
        context.RegisterOperationAction(AnalyzeInterpolation, OperationKind.Interpolation, OperationKind.InterpolatedStringAppendFormatted);
        context.RegisterSymbolAction(AnalyzeRecord, SymbolKind.NamedType);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports code that treats a boxed union as what it holds.
/// A union is a struct:
/// boxing it boxes the union, whose run-time type is the union's, so:
/// <list type="bullet">
/// <item>UN0014: a type test against a case (<c>(object)pet is Cat</c>) is never true (and <c>is not Cat</c> always is), a cast to one throws (and <c>as</c> gives null), <c>pet.Equals(cat)</c> (or <c>cat.Equals(pet)</c>, or <c>Equals(pet, cat)</c>) is false,
/// <c>pet.GetType() == typeof(Cat)</c> is false, and <c>pet.GetType()</c> as a dictionary key never finds what's keyed by a case.</item>
/// <item>UN0015: formatting a union (or a nullable one) as text gives its type name, unless it overrides ToString, and <c>pet.GetType().Name</c> always does.</item>
/// <item>UN0019: a record's generated ToString does the same for each of its public union members, which is reported once for each union declared in source.</item>
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

        // `pet.Equals(cat)`, `cat.Equals(pet)`, `Equals(pet, cat)`, `EqualityComparer<object>.Default.Equals(pet, cat)`: a
        // union only ever equals another of the same union.
        if (method.Name == "Equals" && ComparedForEquality(invocation) is var (union, otherType, reported) && !BoxCanEqual(union, otherType, context.Compilation))
        {
            var name = Name(union, invocation);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, reported.Syntax.GetLocation(), invocation.Syntax.ToString(),
                $"is never true: a {name} only ever equals another {name}. Compare with a {name} made from it, or compare its Value."
            ));
            return;
        }

        // `handlers.TryGetValue(pet.GetType(), out var handler)`, `handlers[pet.GetType()]`
        if (method is { Name: "GetType", Parameters.IsEmpty: true } && invocation.Instance?.Type is { IsUnion: true, IsValueType: true } keyed
            && invocation.Parent is IArgumentOperation { Parameter: { Name: "key", Type: var keyType } } && Symbols.Is(keyType, "Type", 0, "System")
        )
        {
            var name = Name(keyed, invocation);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, invocation.Syntax.GetLocation(), invocation.Syntax.ToString(),
                $"is always typeof({name}), so it never finds what's keyed by the type of what a {name} holds. Look up the type of its Value instead."
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

        // `logger.BeginScope(new Dictionary<string, object> { ["Pet"] = pet })`: the scope's state is formatted, and what's in it.
        if (method is { Name: "BeginScope", IsGenericMethod: true, Parameters.Length: 1 } && IsLogger(method.ContainingType))
        {
            var state = invocation.Arguments[0].Value;
            foreach (var value in state.DescendantsAndSelf().Where(value => value == state || BoxedUnion(value) is not null))
            {
                ReportText(context, value, value);
            }

            return;
        }

        if (!FormatsItsArguments(method))
        {
            return;
        }

        // `Console.WriteLine(pet)`, `builder.Append(pet)`, `string.Format("{0}", pet)`, `logger.LogInformation("{Pet}", pet)`, ...
        foreach (var argument in invocation.Arguments)
        {
            // `string.Join(", ", pets)`, `builder.AppendJoin(", ", pets)`: each element's ToString.
            if (argument.Parameter?.OriginalDefinition.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T }
                && method.IsGenericMethod && ShowsTypeName(method.TypeArguments[0]) is { } elementType
            )
            {
                var sequence = argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;
                var held = Symbols.UnderlyingOfNullable(method.TypeArguments[0]) is null ? "x.Value" : "x?.Value";

                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, sequence.Syntax.GetLocation(), sequence.Syntax.ToString(), Name(elementType, invocation),
                    Remedy($"format '{sequence.Syntax}.Select(x => {held})' instead", elementType)
                ));
                continue;
            }

            foreach (var value in argument.Value is IArrayCreationOperation { IsImplicit: true, Initializer: { } parameters } ? parameters.ElementValues : [argument.Value])
            {
                ReportText(context, value, value);
            }
        }
    }

    /// <summary>
    /// <c>new ArgumentOutOfRangeException(nameof(pet), pet, "...")</c>, whose message shows the actual value.
    /// </summary>
    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (Symbols.Is(creation.Type, "ArgumentOutOfRangeException", 0, "System"))
        {
            foreach (var actualValue in creation.Arguments.Where(argument => argument.Parameter?.Name == "actualValue"))
            {
                ReportText(context, actualValue.Value, actualValue.Value);
            }
        }
    }

    /// <summary>
    /// A union that records' generated ToString prints as its type name, reported once (UN0019):
    /// for a union declared in source, at the union, naming the records, with the members they print as additional locations;
    /// for one from another assembly, which only PrintMembers can fix, at each record that prints it.
    /// </summary>
    /// <param name="printedFromSource">The members records print, for each union declared in source, worked out once.</param>
    private static void AnalyzePrintedUnions(SymbolAnalysisContext context, Lazy<ILookup<ISymbol, ISymbol>> printedFromSource)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (ShowsTypeName(type) is not null && printedFromSource.Value[type].ToList() is { Count: > 0 } printers)
        {
            ReportPrinted(context, type, type.Locations[0], printers, [.. printers.Select(member => member.ContainingType.Name).Distinct()]);
        }

        var fromElsewhere = PrintedUnionMembers(type)
            .Where(printed => printed.Union.DeclaringSyntaxReferences.IsEmpty)
            .GroupBy(printed => (ISymbol)printed.Union, printed => printed.Member, SymbolEqualityComparer.Default);

        foreach (var members in fromElsewhere)
        {
            ReportPrinted(context, (ITypeSymbol)members.Key!, members.First().Locations[0], [.. members.Skip(1)], [type.Name]);
        }
    }

    /// <summary>
    /// <c>pet.GetType().Name</c>, which is always the union's name, whatever it holds.
    /// Reported on the union, which is what to look into instead.
    /// </summary>
    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        if (context.Operation is IPropertyReferenceOperation
            {
                Property.Name: "Name" or "FullName",
                Instance: IInvocationOperation { TargetMethod.Name: "GetType", Instance: { Type: { IsUnion: true, IsValueType: true } union } instance },
            } reference
        )
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, instance.Syntax.GetLocation(), Written(reference.Syntax), Name(union, reference),
                $"use '{Written(instance.Syntax)}.Value?.GetType().{reference.Property.Name}' instead"
            ));
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

    /// <summary>
    /// <c>(object)pet is Cat</c> is never true, and <c>(object)pet is not Cat</c> always is.
    /// </summary>
    private static void AnalyzeTypeTest(OperationAnalysisContext context)
    {
        var (operand, howOften) = context.Operation switch
        {
            IIsTypeOperation { IsNegated: false } isType => (isType.ValueOperand, BoxedUnion(isType.ValueOperand) is { } u && !BoxCanBe(u, isType.TypeOperand, context.Compilation) ? "never" : null),
            IIsPatternOperation isPattern => (isPattern.Value, BoxedUnion(isPattern.Value) is not { } u ? null
                : NeverMatches(isPattern.Pattern, u, context.Compilation) ? "never"
                : isPattern.Pattern is INegatedPatternOperation not && NeverMatches(not.Pattern, u, context.Compilation) ? "always"
                : null),
            _ => (null!, null),
        };

        if (howOften is not null && BoxedUnion(operand) is { } union)
        {
            var name = Name(union, context.Operation);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoxedUnionTest, context.Operation.Syntax.GetLocation(), context.Operation.Syntax.ToString(),
                $"is {howOften} true: a boxed {name} is a {name}, not what it holds. Test the union itself, without boxing it."
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
    /// Whether a boxed <paramref name="union"/> can equal a value of <paramref name="otherType"/> that it's compared with as an object:
    /// unless that's a type that's neither a union nor object, and that a boxed union can't be, it might.
    /// </summary>
    private static bool BoxCanEqual(ITypeSymbol union, ITypeSymbol? otherType, Compilation compilation) =>
        otherType is not { IsUnion: false, SpecialType: not SpecialType.System_Object } type || BoxCanBe(union, type, compilation);

    /// <summary>
    /// For an operation that boxes a struct union (to object, ValueType or an interface), the union's type.
    /// </summary>
    private static ITypeSymbol? BoxedUnion(IOperation operation) =>
        operation is IConversionOperation { Operand.Type: { IsUnion: true, IsValueType: true } union } conversion
        && conversion.GetConversion().IsBoxing
            ? union
            : null;

    /// <summary>
    /// For an <c>Equals</c> call that compares a struct union with another value as objects, which uses the union's own Equals (or the other value's, which a boxed union never equals):
    /// <c>pet.Equals(x)</c>, <c>x.Equals(pet)</c>, the static <c>object.Equals(pet, x)</c>, or <c>EqualityComparer&lt;object&gt;.Default.Equals(pet, x)</c> (either way round).
    /// The union's type, the other value's type, and what to report:
    /// the call, or for <c>x.Equals(pet)</c>, the union, since the Look at what it holds fix rewrites a call as though the union came first.
    /// </summary>
    private static (ITypeSymbol Union, ITypeSymbol? OtherType, IOperation Reported)? ComparedForEquality(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method is { IsStatic: false, Parameters.Length: 1 } && method.Parameters[0].Type.SpecialType == SpecialType.System_Object && invocation.Instance is { } instance)
        {
            var argument = invocation.Arguments[0].Value;
            return instance.Type is { IsUnion: true, IsValueType: true } union ? (union, Unconverted(argument).Type, invocation)
                : BoxedUnion(argument) is { } boxed ? (boxed, instance.Type, argument)
                : null;
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
            ? (leftUnion, Unconverted(right).Type, invocation)
            : BoxedUnion(right) is { } rightUnion
                ? (rightUnion, Unconverted(left).Type, invocation)
                : null;
    }

    /// <summary>
    /// The common ways of turning arguments into text:
    /// Console, TextWriter, StringBuilder, string.Format, string.Concat, string.Join, Convert.ToString, Debug and Trace, an Activity's tags, and a logger's messages and scopes;
    /// a logging method a source generator writes (<c>[LoggerMessage]</c>), or that <c>LoggerMessage.Define</c> makes (a delegate from an ILogger and the values to an Exception);
    /// and a method with a message template, which a logging library marks <c>[MessageTemplateFormatMethod]</c> (Serilog, NLog) or <c>[StructuredMessageTemplate]</c> (JetBrains.Annotations).
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

        if (method.GetAttributes().Any(a => a.AttributeClass?.Name is "LoggerMessageAttribute" or "MessageTemplateFormatMethodAttribute")
            || method.Parameters.Any(p => p.GetAttributes().Any(a => a.AttributeClass?.Name == "StructuredMessageTemplateAttribute"))
            || method is { Name: "Invoke", ContainingType.TypeKind: TypeKind.Delegate, Parameters.Length: > 1 }
                && IsLogger(method.Parameters[0].Type) && Symbols.Is(method.Parameters[method.Parameters.Length - 1].Type, "Exception", 0, "System")
        )
        {
            return true;
        }

        return (type.ContainingNamespace?.ToDisplayString(), type.Name, method.Name) switch
        {
            ("System", "Console", "Write" or "WriteLine") => true,
            ("System.Text", "StringBuilder", "Append" or "AppendLine" or "Insert" or "AppendFormat" or "AppendJoin") => true,
            ("Microsoft.Extensions.Logging", "LoggerExtensions", "Log" or "LogTrace" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError" or "LogCritical" or "BeginScope") => true,
            ("System", "String", "Format" or "Concat" or "Join") => true,
            ("System", "Convert", "ToString") => true,
            ("System.Diagnostics", "Debug" or "Trace", "Write" or "WriteLine" or "Print" or "WriteIf" or "WriteLineIf" or "TraceInformation" or "TraceWarning" or "TraceError") => true,
            ("System.Diagnostics", "Activity", "SetTag" or "AddTag") => true,
            _ => false,
        };
    }

    /// <summary>
    /// Whether the type is Microsoft.Extensions.Logging's ILogger, or implements it.
    /// </summary>
    private static bool IsLogger(ITypeSymbol type) =>
        Symbols.Is(type, "ILogger", 0, "Microsoft", "Extensions", "Logging") || type.AllInterfaces.Any(i => Symbols.Is(i, "ILogger", 0, "Microsoft", "Extensions", "Logging"));

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
    /// The records that print a union, as the start of a message: <c>Owner prints</c>, <c>Owner and Keeper print</c>, <c>Owner, Keeper and 3 more print</c>.
    /// </summary>
    private static string PrintedBy(IReadOnlyList<string> records) => records.Count switch
    {
        1 => $"{records[0]} prints",
        2 => $"{records[0]} and {records[1]} print",
        3 => $"{records[0]}, {records[1]} and {records[2]} print",
        _ => $"{records[0]}, {records[1]} and {records.Count - 2} more print",
    };

    /// <summary>
    /// The public members a record's generated ToString prints with their own ToString that show a union's type name, each with its union.
    /// None for a record that declares its own PrintMembers or ToString, or whose base seals ToString, since then it doesn't generate them.
    /// </summary>
    private static IEnumerable<(ISymbol Member, ITypeSymbol Union)> PrintedUnionMembers(INamedTypeSymbol type)
    {
        static bool SealsToString(INamedTypeSymbol? type) =>
            type is not null && (type.GetMembers("ToString").Any(m => m is IMethodSymbol { Parameters.IsEmpty: true, IsSealed: true }) || SealsToString(type.BaseType));

        if (!type.IsRecord
            || type.GetMembers("PrintMembers").Any(m => !m.IsImplicitlyDeclared)
            || type.GetMembers("ToString").Any(m => m is IMethodSymbol { Parameters.IsEmpty: true, IsImplicitlyDeclared: false })
            || SealsToString(type.BaseType)
        )
        {
            yield break;
        }

        foreach (var member in type.GetMembers())
        {
            var printedType = member switch
            {
                IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null, IsIndexer: false } property => property.Type,
                IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false } field => field.Type,
                _ => null,
            };

            if (ShowsTypeName(printedType) is { } union && !member.Locations.IsEmpty && member.Locations[0].IsInSource)
            {
                yield return (member, union);
            }
        }
    }

    /// <summary>
    /// What to do about a union that shows its type name:
    /// <paramref name="here"/>, or, for a union declared in this compilation, override its ToString, which fixes it everywhere.
    /// </summary>
    private static string Remedy(string here, ITypeSymbol union) =>
        union.DeclaringSyntaxReferences.IsEmpty ? here : $"{here}, or override ToString in {union.Name}";

    /// <summary>
    /// Reports that <paramref name="records"/> print <paramref name="union"/> as its type name (UN0019) at <paramref name="location"/>, with the members in <paramref name="others"/> that print it as additional locations.
    /// </summary>
    private static void ReportPrinted(SymbolAnalysisContext context, ITypeSymbol union, Location location, IEnumerable<ISymbol> others, IReadOnlyList<string> records) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.RecordPrintsUnion, location, others.Select(member => member.Locations[0]), PrintedBy(records),
            union.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Remedy("declare PrintMembers to print its Value", union)
        ));

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
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnionAsText, reported.Syntax.GetLocation(), Written(reported.Syntax), Name(union, reported),
            Remedy($"format '{Written(value.Syntax)}{held}' instead", union)
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

    /// <summary>
    /// Every type in a namespace or type, nested ones included.
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> TypesIn(INamespaceOrTypeSymbol container) =>
        container.GetTypeMembers().SelectMany(type => TypesIn(type).Prepend(type))
            .Concat(container is INamespaceSymbol ns ? ns.GetNamespaceMembers().SelectMany(TypesIn) : []);

    /// <summary>
    /// What a value is before it's converted, as to object to be compared.
    /// </summary>
    private static IOperation Unconverted(IOperation value) =>
        value is IConversionOperation conversion ? conversion.Operand : value;

    /// <summary>
    /// How an expression is written, for a message:
    /// from the start of the <c>?.</c> chain it ends (<c>o?.Status</c>, not <c>.Status</c>).
    /// </summary>
    private static string Written(SyntaxNode syntax)
    {
        var start = syntax;
        while (start.GetFirstToken() is { RawKind: (int)SyntaxKind.DotToken } dot
            && start.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(access => access.WhenNotNull.GetFirstToken() == dot) is { } chain
        )
        {
            start = chain;
        }

        return syntax.SyntaxTree.GetText().ToString(TextSpan.FromBounds(start.SpanStart, syntax.Span.End));
    }

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
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterCompilationStartAction(start =>
        {
            var printedFromSource = new Lazy<ILookup<ISymbol, ISymbol>>(() => TypesIn(start.Compilation.Assembly.GlobalNamespace)
                .SelectMany(PrintedUnionMembers)
                .Where(printed => !printed.Union.DeclaringSyntaxReferences.IsEmpty)
                .ToLookup(printed => (ISymbol)printed.Union.OriginalDefinition, printed => printed.Member, SymbolEqualityComparer.Default)
            );

            start.RegisterSymbolAction(c => AnalyzePrintedUnions(c, printedFromSource), SymbolKind.NamedType);
        });
    }
}

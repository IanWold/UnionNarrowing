using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports where a struct union none of whose cases can be null ends up as its default value, which matches none of the union's case types, so a switch the compiler considers exhaustive throws on it:
/// <list type="bullet">
/// <item>UN0003: <c>default(U)</c>, <c>default</c> and <c>new U()</c>.</item>
/// <item>UN0006: <c>new U[n]</c>, whose elements all start out as the default, and APIs that create, grow, clear or pool arrays.</item>
/// <item>UN0010: methods that fall back to the default, like <c>FirstOrDefault()</c>, or that say so with <c>T?</c> or <c>[MaybeNull]</c>;
/// and a Try method's out value, read when it failed.</item>
/// <item>UN0018: the default of a struct or tuple with such a union inside it.</item>
/// </list>
/// </summary>
/// <remarks>
/// Doesn't report <c>default</c> assigned to an <c>out</c> parameter:
/// that's how a Try method says "nothing here", and the caller isn't supposed to look.
/// Nor a default that's only ever tested (<c>FirstOrDefault() is Cat c</c>, or a switch with a <c>null</c> or discard arm), or put in a local that's assigned again before it's read.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefaultUnionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.DefaultUnion, Descriptors.DefaultUnionArray, Descriptors.DefaultUnionFallback, Descriptors.DefaultStructWithUnion];

    private static void AnalyzeArray(OperationAnalysisContext context)
    {
        var creation = (IArrayCreationOperation)context.Operation;
        if (creation.IsImplicit
            || creation.Initializer is not null
            || creation.Type is not IArrayTypeSymbol { ElementType: var elementType }
            || creation.DimensionSizes.All(size => size.ConstantValue is { HasValue: true, Value: 0 })
            || FilledRightAway(creation)
        )
        {
            return;
        }

        if (CaseSpace.DefaultHoldsNoCase(elementType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionArray, creation.Syntax.GetLocation(), creation.Syntax.ToString(), Display(elementType, creation), "fills the array with"));
        }
        else if (UnionInside(elementType) is { } inside)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultStructWithUnion, creation.Syntax.GetLocation(), creation.Syntax.ToString(), Display(inside.Union, creation), "each element's " + inside.Path));
        }
    }

    private static void AnalyzeDefault(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        if (operation.IsImplicit)
        {
            return;
        }

        // `new Lazy<Pet>()` without a factory makes its Value with the parameterless constructor: the default.
        if (operation is IObjectCreationOperation { Type: INamedTypeSymbol lazy, Constructor: { } lazyConstructor }
            && Symbols.Is(lazy, "Lazy", 1, "System")
            && CaseSpace.DefaultHoldsNoCase(lazy.TypeArguments[0])
            && !lazyConstructor.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Name == "value")
        )
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionFallback, operation.Syntax.GetLocation(), operation.Syntax.ToString(), Display(lazy.TypeArguments[0], operation), "as its Value"));
            return;
        }

        // `new U()` is only the default if it's the implicit parameterless constructor.
        if (operation is IObjectCreationOperation { Constructor: not { Parameters.IsEmpty: true, IsImplicitlyDeclared: true } })
        {
            return;
        }

        var type = operation.Type;
        var direct = CaseSpace.DefaultHoldsNoCase(type);
        var inside = direct ? null : UnionInside(type);
        if ((!direct && inside is null) || IsAssignedToOutParameter(operation) || OnlyTested(operation) || NeverRead(operation, context.CancellationToken))
        {
            return;
        }

        context.ReportDiagnostic(direct
            ? Diagnostic.Create(Descriptors.DefaultUnion, operation.Syntax.GetLocation(), operation.Syntax.ToString(), Display(type!, operation))
            : Diagnostic.Create(Descriptors.DefaultStructWithUnion, operation.Syntax.GetLocation(), operation.Syntax.ToString(), Display(inside!.Value.Union, operation), inside.Value.Path)
        );
    }

    /// <summary>
    /// Library methods that hand back default unions:
    /// by creating one, by filling (part of) an array with them, or by falling back to one.
    /// </summary>
    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var type = method.ContainingType;
        var hasFallback = method.Parameters.Any(p => p.Name == "defaultValue");
        var firstTypeArgument = method.TypeArguments.FirstOrDefault();

        var (descriptor, unionType, detail) = (type.ContainingNamespace?.ToDisplayString(), type.Name, method.Name) switch
        {
            // Creating one.
            ("System", "Activator", "CreateInstance") when method.IsGenericMethod => (Descriptors.DefaultUnion, firstTypeArgument, null),
            // (With constructor arguments, it calls a constructor instead.)
            ("System", "Activator", "CreateInstance") when method.Parameters.Length == 1 || (method.Parameters.Length == 2 && method.Parameters[1].Type.SpecialType == SpecialType.System_Boolean) =>
                (Descriptors.DefaultUnion, TypeOfArgument(invocation), null),
            ("System.Runtime.CompilerServices", "RuntimeHelpers", "GetUninitializedObject") => (Descriptors.DefaultUnion, TypeOfArgument(invocation), null),

            // Arrays of them.
            ("System", "GC", "AllocateArray" or "AllocateUninitializedArray") => (Descriptors.DefaultUnionArray, firstTypeArgument, "fills the array with"),
            ("System", "Array", "CreateInstance") => (Descriptors.DefaultUnionArray, TypeOfArgument(invocation), "fills the array with"),
            ("System", "Array", "Resize") => (Descriptors.DefaultUnionArray, firstTypeArgument, "fills any new elements with"),
            ("System.Runtime.InteropServices", "CollectionsMarshal", "SetCount") => (Descriptors.DefaultUnionArray, firstTypeArgument, "fills any new elements with"),
            ("System", "Array", "Clear") => (Descriptors.DefaultUnionArray, ElementTypeOfFirstArgument(invocation), "resets the elements to"),
            ("System", "Span", "Clear") => (Descriptors.DefaultUnionArray, type.TypeArguments.FirstOrDefault(), "resets the elements to"),
            ("System.Buffers", "ArrayPool", "Rent") => (Descriptors.DefaultUnionArray, type.TypeArguments.FirstOrDefault(), "can return elements set to"),

            // Falling back to one.
            ("System.Linq", "Enumerable" or "Queryable" or "ParallelEnumerable" or "ImmutableArrayExtensions", "FirstOrDefault" or "LastOrDefault" or "SingleOrDefault") when !hasFallback =>
                (Descriptors.DefaultUnionFallback, method.ReturnType, method.Parameters.Any(p => p.Name == "predicate") ? "when nothing matches" : "when the sequence is empty"),
            ("System.Linq", "Enumerable" or "Queryable" or "ParallelEnumerable" or "ImmutableArrayExtensions", "ElementAtOrDefault") => (Descriptors.DefaultUnionFallback, method.ReturnType, "when the index is out of range"),
            ("System.Linq", "Enumerable" or "Queryable" or "ParallelEnumerable", "DefaultIfEmpty") when !hasFallback =>
                (Descriptors.DefaultUnionFallback, firstTypeArgument, "when the sequence is empty"),
            ("System.Collections.Generic", "CollectionExtensions", "GetValueOrDefault") when !hasFallback =>
                (Descriptors.DefaultUnionFallback, method.ReturnType, "when the key isn't there"),
            ("System.Collections.Immutable", "ImmutableDictionary", "GetValueOrDefault") when !hasFallback =>
                (Descriptors.DefaultUnionFallback, method.ReturnType, "when the key isn't there"),
            ("System", "Nullable", "GetValueOrDefault") when method.Parameters.IsEmpty =>
                (Descriptors.DefaultUnionFallback, method.ReturnType, "when it has no value"),

            _ => (null, null, null),
        };

        // A generic method of your own that returns `default` for its type parameter, or any generic method that says
        // it may (`T?`, `[MaybeNull]`).
        if (descriptor is null && (ReturnsDefaultOfTypeParameter(method, context.CancellationToken) || MayReturnDefault(method)))
        {
            (descriptor, unionType, detail) = (Descriptors.DefaultUnionFallback, method.ReturnType, "on some paths");
        }

        AnalyzeOutArguments(context, invocation);
        AnalyzeStoredDefaults(context, invocation);

        if (descriptor is null || unionType is null)
        {
            return;
        }

        // Only what the code goes on to use: not a default that's just tested.
        if (descriptor == Descriptors.DefaultUnionFallback && OnlyTested(invocation))
        {
            return;
        }

        var location = invocation.Syntax.GetLocation();
        if (CaseSpace.DefaultHoldsNoCase(unionType))
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, invocation.Syntax.ToString(), Display(unionType, invocation), detail));
        }
        else if (descriptor != Descriptors.DefaultUnion && UnionInside(unionType) is { } inside)
        {
            var where = descriptor == Descriptors.DefaultUnionArray ? "each element's " + inside.Path : inside.Path;
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultStructWithUnion, location, invocation.Syntax.ToString(), Display(inside.Union, invocation), where));
        }
    }

    /// <summary>
    /// For a Try method's <c>out</c> argument that it says can be the default when it fails (<c>[MaybeNullWhen(false)] out T value</c>), reports the argument when the default would be used:
    /// the call's result is ignored, or the variable is read where the call is known to have failed (<c>if (!TryGet(out var pet)) { ... pet ... }</c>).
    /// </summary>
    private static void AnalyzeOutArguments(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { RefKind: RefKind.Out } parameter
                || parameter.OriginalDefinition.Type is not ITypeParameterSymbol
                || !CaseSpace.DefaultHoldsNoCase(parameter.Type)
                || OutVariable(argument.Value) is not { } local
                || parameter.OriginalDefinition.GetAttributes().FirstOrDefault(a => IsCodeAnalysisAttribute(a, "MaybeNullWhenAttribute")) is not { ConstructorArguments.Length: 1 } maybeNullWhen
                || maybeNullWhen.ConstructorArguments[0].Value is not bool failedResult
            )
            {
                continue;
            }

            if (invocation.Parent is IExpressionStatementOperation
                || ReadWhereCallGave(invocation, failedResult, local, invocation.SemanticModel!, context.CancellationToken)
            )
            {
                var detail = $"when '{invocation.TargetMethod.Name}' returns {(failedResult ? "true" : "false")}";
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionFallback, argument.Syntax.GetLocation(), argument.Syntax.ToString(), Display(parameter.Type, invocation), detail));
            }
        }
    }

    /// <summary>
    /// A generic property that says it can be the default (<c>AsyncLocal&lt;T&gt;.Value</c>, which is until it's set):
    /// <c>T?</c> for an unconstrained T, or <c>[MaybeNull]</c>.
    /// </summary>
    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var definition = reference.Property.OriginalDefinition;
        if (definition.Type is not ITypeParameterSymbol
            || !(definition.NullableAnnotation == NullableAnnotation.Annotated
                || definition.GetAttributes().Any(a => IsCodeAnalysisAttribute(a, "MaybeNullAttribute"))
                || definition.GetMethod?.GetReturnTypeAttributes().Any(a => IsCodeAnalysisAttribute(a, "MaybeNullAttribute")) == true
            )
            || reference.Parent is IAssignmentOperation assignment && assignment.Target == reference
            || !CaseSpace.DefaultHoldsNoCase(reference.Property.Type)
            || OnlyTested(reference)
        )
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionFallback, reference.Syntax.GetLocation(), reference.Syntax.ToString(), Display(reference.Property.Type, reference), "on some paths"));
    }

    /// <summary>
    /// A generic method of your own that stores the default of its type parameter through a parameter (<c>static void Reset&lt;T&gt;(ref T x) =&gt; x = default!;</c>, or into a <c>T[]</c>'s elements), called with a union for it.
    /// </summary>
    private static void AnalyzeStoredDefaults(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var definition = invocation.TargetMethod.OriginalDefinition;
        if (!definition.IsGenericMethod || definition.DeclaringSyntaxReferences.Length != 1)
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { } parameter
                || definition.Parameters.ElementAtOrDefault(parameter.Ordinal) is not { } declared
                || !(declared.Type is ITypeParameterSymbol && declared.RefKind is RefKind.Ref or RefKind.Out
                    || declared.Type is IArrayTypeSymbol { ElementType: ITypeParameterSymbol }
                )
                || !(CaseSpace.DefaultHoldsNoCase(parameter.Type) || parameter.Type is IArrayTypeSymbol { ElementType: var element } && CaseSpace.DefaultHoldsNoCase(element))
                || !StoresDefault(definition, declared, context.CancellationToken)
            )
            {
                continue;
            }

            var unionType = parameter.Type is IArrayTypeSymbol array ? array.ElementType : parameter.Type;
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionFallback, invocation.Syntax.GetLocation(), invocation.Syntax.ToString(),
                Display(unionType, invocation), $"through '{argument.Syntax}' on some paths"
            ));
        }
    }

    private static string Display(ITypeSymbol type, IOperation operation) =>
        type.ToMinimalDisplayString(operation.SemanticModel!, operation.Syntax.SpanStart);

    /// <summary>
    /// The element type of the array passed as the first argument (as an Array).
    /// </summary>
    private static ITypeSymbol? ElementTypeOfFirstArgument(IInvocationOperation invocation)
    {
        var value = invocation.Arguments.FirstOrDefault()?.Value;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value?.Type is IArrayTypeSymbol array ? array.ElementType : null;
    }

    /// <summary>
    /// Whether a new array put in a local is filled right after:
    /// by <c>Array.Fill(array, value)</c>, or by a <c>for (var i = 0; i &lt; array.Length; i++)</c> loop whose body starts with <c>array[i] = ...;</c>.
    /// </summary>
    private static bool FilledRightAway(IArrayCreationOperation creation)
    {
        if (creation.DimensionSizes.Length != 1
            || Outermost(creation).Parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } declarator }
            || declarator.Syntax.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is not { Parent: BlockSyntax block } statement
        )
        {
            return false;
        }

        var index = block.Statements.IndexOf(statement);
        if (index + 1 >= block.Statements.Count)
        {
            return false;
        }

        var name = local.Name;
        var size = creation.DimensionSizes[0].Syntax.ToString();

        switch (block.Statements[index + 1])
        {
            // `array.AsSpan().Fill(value)`, `new Span<Pet>(array).Fill(value)`
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Fill", Expression: var span } } fillCall }
                when fillCall.ArgumentList.Arguments.Count == 1
                    && (span is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsSpan", Expression: IdentifierNameSyntax { Identifier.ValueText: var spanned } }, ArgumentList.Arguments.Count: 0 } && spanned == name
                        || span is ObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: 1 } spanArguments } && spanArguments[0].Expression is IdentifierNameSyntax { Identifier.ValueText: var wrapped } && wrapped == name
                    ):
                return true;

            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Fill" } fill } call }
                when fill.Expression.ToString() is "Array" or "System.Array"
                    && call.ArgumentList.Arguments.Count == 2
                    && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax { Identifier.ValueText: var filled } && filled == name:
                return true;

            case ForStatementSyntax
            {
                Declaration.Variables: { Count: 1 } variables,
                Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression } condition,
                Incrementors.Count: 1,
                Statement: var body,
            } loop when variables[0] is { Initializer.Value: LiteralExpressionSyntax { Token.ValueText: "0" } } counter
                && condition.Left is IdentifierNameSyntax { Identifier.ValueText: var i } && i == counter.Identifier.ValueText
                && (condition.Right.ToString() == name + ".Length" || condition.Right.ToString() == size)
                && (loop.Incrementors[0] switch
                {
                    PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression, Operand: IdentifierNameSyntax post } => post.Identifier.ValueText,
                    PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression, Operand: IdentifierNameSyntax pre } => pre.Identifier.ValueText,
                    _ => null,
                }) == i:
                var first = body is BlockSyntax { Statements.Count: > 0 } inner ? inner.Statements[0] : body;
                return first is ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                        Left: ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: var array }, ArgumentList.Arguments: { Count: 1 } arguments },
                    },
                } && array == name && arguments[0].Expression is IdentifierNameSyntax { Identifier.ValueText: var at } && at == i;

            default:
                return false;
        }
    }

    private static bool IsAssignedToOutParameter(IOperation operation)
    {
        var value = Outermost(operation);
        return value.Parent is ISimpleAssignmentOperation { Target: IParameterReferenceOperation { Parameter.RefKind: RefKind.Out } } assignment
            && assignment.Value == value;
    }

    private static bool IsCodeAnalysisAttribute(AttributeData attribute, string name) =>
        Symbols.IsAttribute(attribute, name, "System", "Diagnostics", "CodeAnalysis");

    /// <summary>
    /// Whether the expression is, on some path through it, the default of <paramref name="typeParameter"/>:
    /// <c>default(T)</c>, or a <c>default</c> literal where a T is wanted (<c>return default;</c>, <c>value = default!;</c> for a T value, <c>flag ? default : value</c>).
    /// </summary>
    private static bool IsDefaultOf(ExpressionSyntax expression, ITypeParameterSymbol typeParameter) =>
        expression switch
        {
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed => IsDefaultOf(suppressed.Operand, typeParameter),
            ParenthesizedExpressionSyntax parenthesized => IsDefaultOf(parenthesized.Expression, typeParameter),
            ConditionalExpressionSyntax conditional => IsDefaultOf(conditional.WhenTrue, typeParameter) || IsDefaultOf(conditional.WhenFalse, typeParameter),
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } => true,
            DefaultExpressionSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: var name } } => name == typeParameter.Name,
            _ => false,
        };

    /// <summary>
    /// Whether a generic method says it can return the default for its type parameter:
    /// a <c>T?</c> return for an unconstrained T, or <c>[return: MaybeNull]</c>.
    /// Not LINQ's Max, Min, MaxBy and MinBy:
    /// they're annotated for the null they return for an empty sequence of a class, but throw for one of a struct.
    /// </summary>
    private static bool MayReturnDefault(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (definition.ReturnType is not ITypeParameterSymbol)
        {
            return false;
        }

        if (definition.Name is "Max" or "Min" or "MaxBy" or "MinBy"
            && definition.ContainingType is { Name: "Enumerable" or "Queryable" or "ParallelEnumerable" or "ImmutableArrayExtensions" } linq
            && Symbols.Is(linq, linq.Name, 0, "System", "Linq")
        )
        {
            return false;
        }

        return definition.ReturnNullableAnnotation == NullableAnnotation.Annotated
            || definition.GetReturnTypeAttributes().Any(a => IsCodeAnalysisAttribute(a, "MaybeNullAttribute"));
    }

    /// <summary>
    /// Whether the default is a local's initial value that nothing reads:
    /// every way through the rest of its block assigns the local before reading it (and nothing captures it).
    /// </summary>
    private static bool NeverRead(IOperation operation, CancellationToken ct)
    {
        var value = Outermost(operation);
        if (value.Parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } declarator }
            || declarator.Syntax.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is not { Parent: BlockSyntax block } statement
            || operation.SemanticModel is not { } model
        )
        {
            return false;
        }

        var index = block.Statements.IndexOf(statement);
        if (index == block.Statements.Count - 1)
        {
            return false;
        }

        var flow = model.AnalyzeDataFlow(block.Statements[index + 1], block.Statements[block.Statements.Count - 1]);
        return flow is { Succeeded: true }
            && !flow.DataFlowsIn.Contains(local, SymbolEqualityComparer.Default)
            && !flow.Captured.Contains(local, SymbolEqualityComparer.Default)
            && flow.WrittenInside.Contains(local, SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// Whether the default only gets tested, which it's fine for:
    /// by a type test or pattern (<c>FirstOrDefault() is Cat c</c>), by a switch statement (where no match just does nothing), or by a switch expression with an arm that matches it (<c>null</c>, a discard, <c>var</c>).
    /// </summary>
    private static bool OnlyTested(IOperation operation)
    {
        var value = Outermost(operation);
        switch (value.Parent)
        {
            case IIsPatternOperation isPattern when isPattern.Value == value:
            case IIsTypeOperation isType when isType.ValueOperand == value:
            case ISwitchOperation switchStatement when switchStatement.Value == value:
                return true;

            case ISwitchExpressionOperation switchExpression when switchExpression.Value == value
                && switchExpression.SemanticModel is { } model
                && CaseSpace.Of(value, model, CancellationToken.None) is { } space:
                return switchExpression.Arms.Any(arm => arm.Guard is null && Facts.Of(arm.Pattern, space, model.Compilation).AlwaysTrue.IncludesNull);

            default:
                return false;
        }
    }

    /// <summary>
    /// The operation, through the conversions it's wrapped in.
    /// </summary>
    private static IOperation Outermost(IOperation operation)
    {
        while (operation.Parent is IConversionOperation conversion)
        {
            operation = conversion;
        }

        return operation;
    }

    private static ILocalSymbol? OutVariable(IOperation value) =>
        value switch
        {
            ILocalReferenceOperation local => local.Local,
            IDeclarationExpressionOperation { Expression: ILocalReferenceOperation local } => local.Local,
            _ => null,
        };

    /// <summary>
    /// Whether <paramref name="local"/> is read in the branch of an <c>if</c> on the call that runs when the call returned <paramref name="result"/> (<c>if (!TryGet(out var x)) { ... x ... }</c>, or the else of <c>if (TryGet(out var x))</c>).
    /// </summary>
    private static bool ReadWhereCallGave(IInvocationOperation invocation, bool result, ILocalSymbol local, SemanticModel model, CancellationToken ct)
    {
        var negated = false;
        IOperation condition = invocation;

        while (condition.Parent is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not)
        {
            negated = !negated;
            condition = not;
        }

        if (condition.Parent is not IConditionalOperation { Condition: var c } ifStatement || c != condition)
        {
            return false;
        }

        // The branch that runs when the condition is true is the one where the call returned !negated.
        var branch = result == !negated ? ifStatement.WhenTrue : ifStatement.WhenFalse;
        return branch is not null && Symbols.References(local, branch.Syntax, model, ct).Any();
    }

    /// <summary>
    /// Whether the method, as declared in source, returns a type parameter and returns its default on some path (<c>return default;</c>, <c>=&gt; default(T)!</c>), so a call with a union for the type parameter can give a default union.
    /// Only the method's own returns count, not those of lambdas or local functions in it.
    /// </summary>
    private static bool ReturnsDefaultOfTypeParameter(IMethodSymbol method, CancellationToken ct)
    {
        var definition = method.OriginalDefinition;
        if (definition.ReturnType is not ITypeParameterSymbol typeParameter)
        {
            return false;
        }

        return definition.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(ct) switch
        {
            MethodDeclarationSyntax declaration => ReturnsDefault(declaration.Body, declaration.ExpressionBody),
            LocalFunctionStatementSyntax declaration => ReturnsDefault(declaration.Body, declaration.ExpressionBody),
            _ => false,
        });

        bool ReturnsDefault(BlockSyntax? body, ArrowExpressionClauseSyntax? arrow) =>
            arrow is not null
                ? IsDefaultOf(arrow.Expression, typeParameter)
                : body is not null
                    && body
                    .DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                    .OfType<ReturnStatementSyntax>()
                    .Any(statement => statement.Expression is { } expression && IsDefaultOf(expression, typeParameter));
    }

    /// <summary>
    /// Whether the method's body assigns <c>default</c> to the parameter (or to an element of it).
    /// </summary>
    private static bool StoresDefault(IMethodSymbol method, IParameterSymbol parameter, CancellationToken ct)
    {
        if ((parameter.Type as ITypeParameterSymbol ?? (parameter.Type as IArrayTypeSymbol)?.ElementType as ITypeParameterSymbol) is not { } typeParameter)
        {
            return false;
        }

        var body = method.DeclaringSyntaxReferences[0].GetSyntax(ct);
        return body.DescendantNodes(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && IsDefaultOf(assignment.Right, typeParameter)
                && (assignment.Left is ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax target } ? target : assignment.Left as IdentifierNameSyntax)
                    is { Identifier.ValueText: var name } && name == parameter.Name
            );
    }

    /// <summary>
    /// The type in a <c>typeof(T)</c> first argument.
    /// </summary>
    private static ITypeSymbol? TypeOfArgument(IInvocationOperation invocation) =>
        invocation.Arguments.FirstOrDefault()?.Value is ITypeOfOperation { TypeOperand: var type } ? type : null;

    /// <summary>
    /// For a struct (or tuple) that isn't a union itself, the first union inside it, through its fields (and its auto-properties' fields) and theirs, whose default holds no case:
    /// the union type and where it is (<c>Entry.Pet</c>).
    /// Fields of structs from other assemblies aren't all visible, so those aren't looked into.
    /// </summary>
    private static (ITypeSymbol Union, string Path)? UnionInside(ITypeSymbol? type, int depth = 0)
    {
        if (type is not INamedTypeSymbol { IsValueType: true, IsUnion: false, TypeKind: TypeKind.Struct } named
            || named.SpecialType != SpecialType.None
            || named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || depth >= Limits.StructNesting
            || (!named.IsTupleType && named.DeclaringSyntaxReferences.IsEmpty)
        )
        {
            return null;
        }

        var fields = named.IsTupleType
            ? named.TupleElements.AsEnumerable()
            : named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst);

        foreach (var field in fields)
        {
            var name = field.AssociatedSymbol?.Name ?? field.Name;
            var where = (named.IsTupleType ? "(...)" : named.Name) + "." + name;
            
            if (CaseSpace.DefaultHoldsNoCase(field.Type))
            {
                return (field.Type, where);
            }

            if (UnionInside(field.Type, depth + 1) is { } inner)
            {
                return (inner.Union, where + inner.Path.Substring(inner.Path.IndexOf('.')));
            }
        }

        return null;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeDefault, OperationKind.DefaultValue, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeArray, OperationKind.ArrayCreation);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }
}

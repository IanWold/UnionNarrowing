using System;
using System.Collections.Generic;
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
/// <item>UN0010: methods that fall back to the default, like <c>FirstOrDefault()</c>, or that say so with <c>T?</c>, <c>[MaybeNull]</c> or a <c>Task&lt;T?&gt;</c>;
/// and a Try method's out value, read when it failed.</item>
/// <item>UN0018: the default of a struct or tuple with such a union inside it, unless an object initializer (or <c>with</c>) sets every one.</item>
/// </list>
/// </summary>
/// <remarks>
/// Doesn't report <c>default</c> assigned to an <c>out</c> parameter:
/// that's how a Try method says "nothing here", and the caller isn't supposed to look.
/// Nor <c>default!</c> (or <c>new Pet[n]!</c>), which says the default is meant;
/// nor a default that's only ever tested (<c>FirstOrDefault() is Cat c</c>, or a switch with a <c>null</c> or discard arm), put in a local that's assigned again before it's read, or checked for before every use of it.
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
            || IsForgiven(creation)
            || FilledRightAway(creation)
        )
        {
            return;
        }

        if (CaseSpace.DefaultHoldsNoCase(elementType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.DefaultUnionArray, creation.Syntax.GetLocation(), creation.Syntax.ToString(), Display(elementType, creation), "fills the array with", Forgiving(creation)));
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

        // An object initializer (or `with`) can set what's inside: `new Entry { Pet = pet }`.
        var initializer = operation is IObjectCreationOperation creation
            ? creation.Initializer
            : (Outermost(operation).Parent as IWithOperation)?.Initializer;

        var type = operation.Type;
        var direct = CaseSpace.DefaultHoldsNoCase(type);
        var inside = direct ? null : UnionInside(type, initializer);
        if ((!direct && inside is null) || IsForgiven(operation) || IsAssignedToOutParameter(operation) || OnlyTested(operation) || NeverRead(operation, context.CancellationToken))
        {
            return;
        }

        context.ReportDiagnostic(direct
            ? Diagnostic.Create(Descriptors.DefaultUnion, operation.Syntax.GetLocation(), operation.Syntax.ToString(), Display(type!, operation), Forgiving(operation))
            : Diagnostic.Create(Descriptors.DefaultStructWithUnion, operation.Syntax.GetLocation(), operation.Syntax.ToString(), Display(inside!.Value.Union, operation), inside.Value.Path)
        );
    }

    /// <summary>
    /// Library methods that hand back default unions:
    /// by creating one, by filling (part of) an array with them, or by falling back to one.
    /// </summary>
    private static void AnalyzeInvocation(OperationAnalysisContext context, Session session)
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
        // it may (`T?`, `[MaybeNull]`, `Task<T?>`).
        if (descriptor is null && (ReturnsDefaultOfTypeParameter(method, context.CancellationToken) || MayReturnDefault(method)))
        {
            (descriptor, unionType, detail) = (Descriptors.DefaultUnionFallback, TaskResult(method.ReturnType) ?? method.ReturnType, "on some paths");
        }

        AnalyzeOutArguments(context, invocation);
        AnalyzeStoredDefaults(context, invocation);

        if (descriptor is null || unionType is null)
        {
            return;
        }

        // Only what the code goes on to use: not a fallback that's just tested, or checked for before it's used, nor a
        // default said to be meant (`ArrayPool<Pet>.Shared.Rent(n)!`).
        var given = Given(invocation);
        if (descriptor == Descriptors.DefaultUnionFallback ? OnlyTested(given) || CheckedBeforeUse(given, session, context.CancellationToken) : IsForgiven(invocation))
        {
            return;
        }

        var location = given.Syntax.GetLocation();
        if (CaseSpace.DefaultHoldsNoCase(unionType))
        {
            var (code, display) = (given.Syntax.ToString(), Display(unionType, invocation));
            context.ReportDiagnostic(
                descriptor == Descriptors.DefaultUnion ? Diagnostic.Create(descriptor, location, code, display, Forgiving(invocation))
                : descriptor == Descriptors.DefaultUnionArray ? Diagnostic.Create(descriptor, location, code, display, detail, Forgiving(invocation))
                : Diagnostic.Create(descriptor, location, code, display, detail)
            );
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
                || ReadWhereCallGave(invocation, failedResult, local, invocation.SemanticModel!)
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

    /// <summary>
    /// Whether a pattern on a union declares a variable that holds the union itself (<c>is { } found</c>, <c>is var found</c>), which a default union gets into:
    /// <c>{ }</c> is always true for a struct.
    /// </summary>
    private static bool BindsTheValue(IPatternOperation pattern, ITypeSymbol? type) => CaseSpace.DefaultHoldsNoCase(type) && pattern.DescendantsAndSelf().Any(part => part switch
    {
        IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol local } => SymbolEqualityComparer.Default.Equals(local.Type, type),
        IRecursivePatternOperation { DeclaredSymbol: ILocalSymbol local } => SymbolEqualityComparer.Default.Equals(local.Type, type),
        _ => false,
    });

    /// <summary>
    /// Whether the default is put in a local whose every use checks for it first (<c>var last = changes.LastOrDefault(); if (last is null) return null;</c>):
    /// each read of the local only tests it (see <see cref="OnlyTested"/>), is in a condition that tells the default apart (see <see cref="TellsTheDefaultApart"/>), or comes where earlier code rules the default out.
    /// </summary>
    private static bool CheckedBeforeUse(IOperation given, Session session, CancellationToken ct)
    {
        if (Outermost(given).Parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } declarator }
            || declarator.Syntax.FirstAncestorOrSelf<BlockSyntax>() is not { } scope
            || given.SemanticModel is not { } model
        )
        {
            return false;
        }

        return Symbols.References(local, scope, model, ct)
            .Where(name => !WriteAnalysis.IsWrite(name, model, ct))
            .All(name => model.GetOperation(name, ct) is { } read && CaseSpace.Of(read, model, ct) is { } space
                && (OnlyTested(read) || TellsTheDefaultApart(read, space, session) || Narrowing.ExcludedBefore(read.Syntax, read, space, model, session, ct).IncludesNull)
            );
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
    /// Whether the first of <paramref name="statements"/> that uses the array named <paramref name="array"/> fills it (see <see cref="Fills"/>).
    /// </summary>
    private static bool FilledFirst(IEnumerable<StatementSyntax> statements, string array, string size) =>
        statements.FirstOrDefault(statement => Mentions(statement, array)) is { } first && Fills(first, array, size);

    /// <summary>
    /// Whether a new array put in a local is filled before anything reads it:
    /// the first statement after it that uses the array fills it (see <see cref="Fills"/>).
    /// </summary>
    private static bool FilledRightAway(IArrayCreationOperation creation) =>
        creation.DimensionSizes.Length == 1
        && Outermost(creation).Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } declarator }
        && declarator.Syntax.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is { Parent: BlockSyntax block } statement
        && FilledFirst(block.Statements.Skip(block.Statements.IndexOf(statement) + 1), local.Name, creation.DimensionSizes[0].Syntax.ToString());

    /// <summary>
    /// Whether a statement fills the array named <paramref name="array"/>, of length <paramref name="size"/>, before it reads it:
    /// <c>Array.Fill(array, value)</c>, <c>array.AsSpan().Fill(value)</c> or <c>new Span&lt;Pet&gt;(array).Fill(value)</c>;
    /// a copy into it (<c>Array.Copy(source, array, n)</c>, <c>source.CopyTo(array)</c>);
    /// a <c>for (var i = 0; i &lt; array.Length; i++)</c> loop that sets <c>array[i]</c> before it uses the array any other way, or a <c>foreach</c> loop that sets <c>array[n++]</c> that way;
    /// or an <c>if</c> each of whose branches fills it.
    /// </summary>
    private static bool Fills(StatementSyntax statement, string array, string size)
    {
        bool IsTheArray(ExpressionSyntax expression) =>
            expression is IdentifierNameSyntax { Identifier.ValueText: var name } && name == array;

        bool SetsAnElementFirst(StatementSyntax body, Func<ExpressionSyntax, bool> isTheIndex) =>
            (body is BlockSyntax block ? block.Statements.FirstOrDefault(s => Mentions(s, array)) : body) is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: ElementAccessExpressionSyntax { Expression: var target, ArgumentList.Arguments: { Count: 1 } indexes },
                    Right: var value,
                },
            }
            && IsTheArray(target) && isTheIndex(indexes[0].Expression) && !Mentions(value, array);

        switch (statement)
        {
            case BlockSyntax block:
                return FilledFirst(block.Statements, array, size);

            case IfStatementSyntax { Else: { } otherwise } ifStatement:
                return !Mentions(ifStatement.Condition, array) && Fills(ifStatement.Statement, array, size) && Fills(otherwise.Statement, array, size);

            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call, ArgumentList.Arguments: var arguments } }:
                var destination = (call.Name.Identifier.ValueText, call.Expression.ToString(), arguments.Count) switch
                {
                    ("Fill", "Array" or "System.Array", 2) => 0,
                    ("Copy", "Array" or "System.Array", 3) => 1,
                    ("Copy" or "ConstrainedCopy", "Array" or "System.Array", 5) => 2,
                    ("CopyTo", _, 1 or 2) => 0,
                    _ => (int?)null,
                };

                // `array.AsSpan().Fill(value)`, `new Span<Pet>(array).Fill(value)`
                var spanFilled = call.Name.Identifier.ValueText == "Fill" && arguments.Count == 1 && call.Expression switch
                {
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsSpan", Expression: var spanned }, ArgumentList.Arguments.Count: 0 } => IsTheArray(spanned),
                    ObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: 1 } wrapped } => IsTheArray(wrapped[0].Expression),
                    _ => false,
                };

                return spanFilled && !Mentions(arguments[0], array)
                    || destination is { } at && IsTheArray(arguments[at].Expression) && !Mentions(call.Expression, array) && arguments.Where((_, i) => i != at).All(a => !Mentions(a, array));

            case ForStatementSyntax
            {
                Declaration.Variables: { Count: 1 } variables,
                Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LessThanExpression } condition,
                Incrementors.Count: 1,
            } loop when variables[0] is { Initializer.Value: LiteralExpressionSyntax { Token.ValueText: "0" } } counter
                && condition.Left is IdentifierNameSyntax { Identifier.ValueText: var i } && i == counter.Identifier.ValueText
                && (condition.Right.ToString() == array + ".Length" || condition.Right.ToString() == size)
                && (loop.Incrementors[0] switch
                {
                    PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression, Operand: IdentifierNameSyntax post } => post.Identifier.ValueText,
                    PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression, Operand: IdentifierNameSyntax pre } => pre.Identifier.ValueText,
                    _ => null,
                }) == i:
                return SetsAnElementFirst(loop.Statement, index => index is IdentifierNameSyntax { Identifier.ValueText: var at } && at == i);

            case ForEachStatementSyntax loop when !Mentions(loop.Expression, array):
                return SetsAnElementFirst(loop.Statement, index => index is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression, Operand: IdentifierNameSyntax });

            default:
                return false;
        }
    }

    /// <summary>
    /// What gives a call's result: the call, or for a task that's awaited right there (through <c>ConfigureAwait</c> too), the <c>await</c>.
    /// </summary>
    private static IOperation Given(IInvocationOperation invocation)
    {
        var task = invocation.Parent is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } configured && configured.Instance == invocation ? configured : (IOperation)invocation;
        return TaskResult(invocation.TargetMethod.ReturnType) is not null && task.Parent is IAwaitOperation awaited ? awaited : invocation;
    }

    /// <summary>
    /// The end of a message that says how to say the default is meant:
    /// the null-forgiving operator after what gives it (<c>default!</c>, <c>new Pet[n]!</c>), where that has a value to put it on.
    /// </summary>
    private static string Forgiving(IOperation operation) =>
        operation.Type is { SpecialType: not SpecialType.System_Void } ? $". Write '{operation.Syntax}!' where that's meant" : "";

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
    /// <c>default(T)</c>, or a <c>default</c> literal where a T is wanted (<c>return default;</c>, <c>value = default!;</c> for a T value, <c>flag ? default : value</c>,
    /// and <c>value ?? default</c> for a <c>T?</c> value where T is a struct, which is null without one).
    /// </summary>
    private static bool IsDefaultOf(ExpressionSyntax expression, ITypeParameterSymbol typeParameter) =>
        expression switch
        {
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed => IsDefaultOf(suppressed.Operand, typeParameter),
            ParenthesizedExpressionSyntax parenthesized => IsDefaultOf(parenthesized.Expression, typeParameter),
            ConditionalExpressionSyntax conditional => IsDefaultOf(conditional.WhenTrue, typeParameter) || IsDefaultOf(conditional.WhenFalse, typeParameter),
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce => typeParameter.HasValueTypeConstraint && IsDefaultOf(coalesce.Right, typeParameter),
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } => true,
            DefaultExpressionSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: var name } } => name == typeParameter.Name,
            _ => false,
        };

    /// <summary>
    /// Whether the null-forgiving operator says the default is meant (<c>default!</c>).
    /// </summary>
    private static bool IsForgiven(IOperation operation)
    {
        var syntax = operation.Syntax;
        while (syntax.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            syntax = parenthesized;
        }

        return syntax.Parent is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression };
    }

    /// <summary>
    /// Whether a generic method says it can return the default for its type parameter:
    /// a <c>T?</c> return for an unconstrained T, or <c>[return: MaybeNull]</c>, or a task of a <c>T?</c> (<c>Task&lt;T?&gt; GetFromJsonAsync&lt;T&gt;(...)</c>).
    /// Not LINQ's Max, Min, MaxBy and MinBy:
    /// they're annotated for the null they return for an empty sequence of a class, but throw for one of a struct.
    /// </summary>
    private static bool MayReturnDefault(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (definition.Name is "Max" or "Min" or "MaxBy" or "MinBy"
            && definition.ContainingType is { Name: "Enumerable" or "Queryable" or "ParallelEnumerable" or "ImmutableArrayExtensions" } linq
            && Symbols.Is(linq, linq.Name, 0, "System", "Linq")
        )
        {
            return false;
        }

        return definition.ReturnType is ITypeParameterSymbol
            ? definition.ReturnNullableAnnotation == NullableAnnotation.Annotated || definition.GetReturnTypeAttributes().Any(a => IsCodeAnalysisAttribute(a, "MaybeNullAttribute"))
            : TaskResult(definition.ReturnType) is ITypeParameterSymbol { NullableAnnotation: NullableAnnotation.Annotated };
    }

    /// <summary>
    /// Whether <paramref name="node"/> names <paramref name="name"/> anywhere in it.
    /// </summary>
    private static bool Mentions(SyntaxNode node, string name) =>
        node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.ValueText == name);

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
    /// by a type test or pattern (<c>FirstOrDefault() is Cat c</c>, but not <c>is { } found</c>, which puts it in a variable: see <see cref="BindsTheValue"/>), by a switch statement (where no match just does nothing), or by a switch expression with an arm that matches it (<c>null</c>, a discard, <c>var</c>).
    /// </summary>
    private static bool OnlyTested(IOperation operation)
    {
        var value = Outermost(operation);
        switch (value.Parent)
        {
            case IIsPatternOperation isPattern when isPattern.Value == value:
                return !BindsTheValue(isPattern.Pattern, value.Type);

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
    /// Whether the value the call put in <paramref name="local"/> is read where the call returned <paramref name="result"/>:
    /// in the branch of an <c>if</c> on the call that runs then (<c>if (!TryGet(out var x)) { ... x ... }</c>, or the else of <c>if (TryGet(out var x))</c>),
    /// or after the <c>if</c>, where that branch (if it has one) can get to its end without assigning the local (<c>if (!TryGet(out var x)) { Log(); } ... x ...</c>).
    /// Not where the branch assigns the local before reading it (<c>if (!TryGet(out var x)) x = fallback;</c>).
    /// </summary>
    private static bool ReadWhereCallGave(IInvocationOperation invocation, bool result, ILocalSymbol local, SemanticModel model)
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
        var flow = branch?.Syntax switch
        {
            StatementSyntax statement => model.AnalyzeDataFlow(statement),
            ExpressionSyntax expression => model.AnalyzeDataFlow(expression),
            _ => null,
        };

        if (flow is { Succeeded: true } && flow.DataFlowsIn.Contains(local, SymbolEqualityComparer.Default))
        {
            return true;
        }

        var getsPast = branch?.Syntax is not StatementSyntax statementBranch
            || model.AnalyzeControlFlow(statementBranch) is { Succeeded: true, EndPointIsReachable: true } && flow?.AlwaysAssigned.Contains(local, SymbolEqualityComparer.Default) != true;

        return getsPast
            && ifStatement.Syntax is IfStatementSyntax { Parent: BlockSyntax block } ifSyntax
            && block.Statements.IndexOf(ifSyntax) is var index && index < block.Statements.Count - 1
            && model.AnalyzeDataFlow(block.Statements[index + 1], block.Statements[block.Statements.Count - 1]) is { Succeeded: true } after
            && after.DataFlowsIn.Contains(local, SymbolEqualityComparer.Default);
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
    /// Whether an object initializer sets the field at the end of <paramref name="path"/>:
    /// it assigns the first field (or its property), or gives it a nested initializer that sets the rest.
    /// </summary>
    private static bool Sets(IObjectOrCollectionInitializerOperation? initializer, IReadOnlyList<IFieldSymbol> path)
    {
        bool IsFirst(ISymbol member) =>
            SymbolEqualityComparer.Default.Equals(member.OriginalDefinition, (path[0].AssociatedSymbol ?? path[0]).OriginalDefinition);

        return initializer is not null && initializer.Initializers.Any(set => set switch
        {
            ISimpleAssignmentOperation { Target: IMemberReferenceOperation target } => IsFirst(target.Member),
            IMemberInitializerOperation { InitializedMember: IMemberReferenceOperation member, Initializer: var nested } => path.Count > 1 && IsFirst(member.Member) && Sets(nested, [.. path.Skip(1)]),
            _ => false,
        });
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
    /// For a <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>, the T that awaiting it gives.
    /// </summary>
    private static ITypeSymbol? TaskResult(ITypeSymbol? type) =>
        type is INamedTypeSymbol { TypeArguments.Length: 1 } task
        && (Symbols.Is(task, "Task", 1, "System", "Threading", "Tasks") || Symbols.Is(task, "ValueTask", 1, "System", "Threading", "Tasks"))
            ? task.TypeArguments[0]
            : null;

    /// <summary>
    /// Whether a read of a value is part of a test in an <c>if</c>'s (or <c>?:</c>'s) condition that's sure to be one way for the default, as <see cref="Facts"/> reads it (<c>!top.IsTopLevel</c>, with <c>IsTopLevel =&gt; this is TopLevel</c>):
    /// a test of the default, not a use of it.
    /// The test is as much of the condition as is between <c>!</c>, <c>&amp;&amp;</c> and <c>||</c>.
    /// </summary>
    private static bool TellsTheDefaultApart(IOperation read, CaseSpace space, Session session)
    {
        static bool Combines(IOperation operation) =>
            operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } or IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr };

        var test = read;
        while (test.Parent is { } parent && !Combines(parent) && parent is not IConditionalOperation)
        {
            test = parent;
        }

        var condition = test;
        while (condition.Parent is { } parent && Combines(parent))
        {
            condition = parent;
        }

        return condition.Parent is IConditionalOperation conditional && conditional.Condition == condition
            && TrackedValue.From(read, session) is { } value
            && Facts.Of(test, value, space, read.SemanticModel!.Compilation) is var outcome
            && (outcome.AlwaysTrue.IncludesNull || outcome.AlwaysFalse.IncludesNull);
    }

    /// <summary>
    /// The type in a <c>typeof(T)</c> first argument.
    /// </summary>
    private static ITypeSymbol? TypeOfArgument(IInvocationOperation invocation) =>
        invocation.Arguments.FirstOrDefault()?.Value is ITypeOfOperation { TypeOperand: var type } ? type : null;

    /// <summary>
    /// For a struct (or tuple) that isn't a union itself, the unions inside it whose default holds no case, through its fields (and its auto-properties' fields) and theirs:
    /// each one's type, and the fields that lead to it.
    /// Not through a ref field, whose default refers to nothing, nor into a struct from another assembly, whose fields aren't all visible.
    /// </summary>
    private static IEnumerable<(ITypeSymbol Union, ImmutableArray<IFieldSymbol> Fields)> UnionsInside(ITypeSymbol? type, int depth = 0)
    {
        if (type is not INamedTypeSymbol { IsValueType: true, IsUnion: false, TypeKind: TypeKind.Struct } named
            || named.SpecialType != SpecialType.None
            || named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || depth >= Limits.StructNesting
            || (!named.IsTupleType && named.DeclaringSyntaxReferences.IsEmpty)
        )
        {
            yield break;
        }

        var fields = named.IsTupleType
            ? named.TupleElements.AsEnumerable()
            : named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst && f.RefKind == RefKind.None);

        foreach (var field in fields)
        {
            if (CaseSpace.DefaultHoldsNoCase(field.Type))
            {
                yield return (field.Type, [field]);
                continue;
            }

            foreach (var (union, inner) in UnionsInside(field.Type, depth + 1))
            {
                yield return (union, inner.Insert(0, field));
            }
        }
    }

    /// <summary>
    /// For a struct (or tuple) that isn't a union itself, the first union inside it whose default holds no case (see <see cref="UnionsInside"/>) that <paramref name="initializer"/> doesn't set:
    /// the union type and where it is (<c>Entry.Pet</c>).
    /// </summary>
    /// <param name="initializer">The object initializer, or <c>with</c>'s, that the struct is created with, if any.</param>
    internal static (ITypeSymbol Union, string Path)? UnionInside(ITypeSymbol? type, IObjectOrCollectionInitializerOperation? initializer = null)
    {
        foreach (var (union, fields) in UnionsInside(type))
        {
            if (!Sets(initializer, fields))
            {
                return (union, (type!.IsTupleType ? "(...)" : type.Name) + "." + string.Join(".", fields.Select(f => f.AssociatedSymbol?.Name ?? f.Name)));
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
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterCompilationStartAction(start =>
        {
            var session = new Session(start.Options.AnalyzerConfigOptionsProvider);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, session), OperationKind.Invocation);
        });
    }
}

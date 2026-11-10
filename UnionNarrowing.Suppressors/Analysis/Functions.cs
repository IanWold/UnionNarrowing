using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Function boundaries, and what's known about calls:
/// where a function can be called from, and whether a call ever returns.
/// </summary>
internal static class Functions
{
    /// <summary>
    /// Whether every call to the method throws, going by its source:
    /// its body is a <c>throw</c>, or ends in one with no <c>return</c> anywhere.
    /// Only for a method the call can't be dispatched away from (not virtual, abstract or an interface member), that runs when called (not async or an iterator).
    /// </summary>
    private static bool AlwaysThrows(IMethodSymbol method)
    {
        static bool Throws(BlockSyntax? body, ArrowExpressionClauseSyntax? arrow)
        {
            if (arrow is not null)
            {
                return arrow.Expression is ThrowExpressionSyntax;
            }

            if (body is not { Statements.Count: > 0 } || body.Statements[body.Statements.Count - 1] is not ThrowStatementSyntax)
            {
                return false;
            }

            return !body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .Any(n => n is ReturnStatementSyntax or YieldStatementSyntax);
        }

        method = method.ReducedFrom ?? method.OriginalDefinition;

        if (CanBeDispatchedElsewhere(method) || method.IsAsync)
        {
            return false;
        }

        return method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() switch
        {
            MethodDeclarationSyntax declaration => Throws(declaration.Body, declaration.ExpressionBody),
            LocalFunctionStatementSyntax declaration => Throws(declaration.Body, declaration.ExpressionBody),
            _ => false,
        });
    }

    /// <summary>
    /// The expression body (of a member, local function or lambda) the expression is in, if it's in one rather than in a statement.
    /// </summary>
    public static ExpressionSyntax? BodyExpression(ExpressionSyntax expression)
    {
        for (SyntaxNode? node = expression; node is not null && !IsFunction(node); node = node.Parent)
        {
            if (node.Parent is ArrowExpressionClauseSyntax arrow && arrow.Expression == node)
            {
                return arrow.Expression;
            }

            if (node.Parent is AnonymousFunctionExpressionSyntax lambda && lambda.ExpressionBody == node)
            {
                return lambda.ExpressionBody;
            }
        }

        return null;
    }

    /// <summary>
    /// The names of methods the compiler calls by pattern, with nothing in the source naming them:
    /// a collection initializer's Add, an interpolated string handler's Append methods, Deconstruct, foreach's GetEnumerator and MoveNext, await's GetAwaiter and GetResult, using's Dispose, query clauses' Select and the rest, a range's Slice, fixed's GetPinnableReference, and a custom union's TryGetValue.
    /// </summary>
    private static readonly ImmutableHashSet<string> CalledByPattern = ImmutableHashSet.Create(
        "Add", "AppendLiteral", "AppendFormatted", "Deconstruct", "GetEnumerator", "GetAsyncEnumerator", "MoveNext", "MoveNextAsync", "Dispose", "DisposeAsync",
        "GetAwaiter", "GetResult", "OnCompleted", "UnsafeOnCompleted", "Select", "SelectMany", "Where", "Join", "GroupJoin", "OrderBy", "OrderByDescending",
        "ThenBy", "ThenByDescending", "GroupBy", "Cast", "Slice", "GetPinnableReference", "TryGetValue"
    );

    /// <summary>
    /// Whether the method implements an interface member:
    /// for its own type, or, being public, for a type in the compilation that derives from its type.
    /// </summary>
    private static bool ImplementsAnInterfaceMember(IMethodSymbol function, INamedTypeSymbol type, Compilation compilation)
    {
        bool ImplementsOneFor(INamedTypeSymbol implementing) =>
            implementing.AllInterfaces.SelectMany(i => i.GetMembers()).Any(member => SymbolEqualityComparer.Default.Equals(implementing.FindImplementationForInterfaceMember(member)?.OriginalDefinition, function));

        bool DerivesFromType(INamedTypeSymbol derived)
        {
            for (var baseType = derived.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, type))
                {
                    return true;
                }
            }

            return false;
        }

        return ImplementsOneFor(type)
            || (function is { DeclaredAccessibility: Accessibility.Public, IsStatic: false } && !type.IsSealed
                && compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>().Any(derived => DerivesFromType(derived) && ImplementsOneFor(derived))
            );
    }

    /// <summary>
    /// Where every call to the function can be:
    /// for a local function, the function it's in;
    /// for a private method (or constructor), every part of its type;
    /// for one nothing outside the assembly can see (internal, or in an internal type), every file, as long as the assembly doesn't show its internals to others.
    /// Null for a method anything else could call:
    /// one that's virtual, implements an interface member, is the entry point, is partial, is one the compiler calls by pattern (see <see cref="CalledByPattern"/>), or is the target of an <c>[UnsafeAccessor]</c>;
    /// and for a record's copy constructor (which <c>with</c> calls), an attribute's constructor and an interpolated string handler's (which interpolated strings call).
    /// </summary>
    public static List<SyntaxNode>? CallerScopes(IMethodSymbol function, SyntaxNode body, SemanticModel model, Session session, CancellationToken ct)
    {
        if (function.MethodKind == MethodKind.LocalFunction)
        {
            return EnclosingFunction(body) is { } enclosing ? [enclosing] : null;
        }

        var compilation = model.Compilation;
        if (function.MethodKind == MethodKind.Constructor
            && (function.IsImplicitlyDeclared
                || function.ContainingType is not { TypeKind: TypeKind.Class or TypeKind.Struct } constructed
                || (constructed.IsRecord && function.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(function.Parameters[0].Type, constructed))
                || Symbols.InheritsFrom(constructed, "Attribute")
                || Symbols.HasAttribute(constructed, "InterpolatedStringHandlerAttribute", "System", "Runtime", "CompilerServices")
            )
        )
        {
            return null;
        }

        if (function.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)
            || CanBeDispatchedElsewhere(function)
            || function.IsPartialDefinition || function.PartialImplementationPart is not null || function.PartialDefinitionPart is not null
            || !function.ExplicitInterfaceImplementations.IsEmpty
            || function.ContainingType is not { } type
            || (function.MethodKind == MethodKind.Ordinary && CalledByPattern.Contains(function.Name))
            || ImplementsAnInterfaceMember(function, type, compilation)
            || session.IsUnsafeAccessorTarget(function.Name, compilation, ct)
            || SymbolEqualityComparer.Default.Equals(compilation.GetEntryPoint(ct), function)
            || !type.DeclaringSyntaxReferences.All(r => compilation.SyntaxTrees.Contains(r.SyntaxTree))
        )
        {
            return null;
        }

        if (function.DeclaredAccessibility == Accessibility.Private)
        {
            return [.. type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(ct))];
        }

        var visibleOutside = true;
        for (ISymbol? symbol = function; symbol is not null and not INamespaceSymbol; symbol = symbol.ContainingType)
        {
            if (symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal)
            {
                visibleOutside = false;
            }
        }

        var showsInternals = Symbols.HasAttribute(compilation.Assembly, "InternalsVisibleToAttribute", "System", "Runtime", "CompilerServices");
        return visibleOutside || showsInternals ? null : [.. compilation.SyntaxTrees.Select(tree => tree.GetRoot(ct))];
    }

    /// <summary>
    /// Whether a call to the member could run something other than its own declaration's body:
    /// it's virtual, abstract, an override, extern, or an interface member.
    /// </summary>
    public static bool CanBeDispatchedElsewhere(ISymbol member) =>
        member.IsVirtual || member.IsAbstract || member.IsOverride || member.IsExtern || member.ContainingType is null or { TypeKind: TypeKind.Interface };

    /// <summary>
    /// Whether a <c>with</c> copies without running code of its own:
    /// on a struct, it copies the bits; on a record, it calls the copy constructor, which the compiler has to have generated (in it and the records it derives from), since one written by hand can do anything.
    /// </summary>
    public static bool CopiesAsIs(IWithOperation with)
    {
        for (var type = with.CloneMethod is null ? null : with.Type as INamedTypeSymbol; type is { IsRecord: true }; type = type.BaseType)
        {
            if (type.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared && c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The places a constructor is called, in <paramref name="scope"/>:
    /// <c>new</c> (or target-typed <c>new(...)</c>), <c>: this(...)</c> and <c>: base(...)</c>, and a primary constructor's base call.
    /// Those are the only ways to call one, except for a record's copy constructor, which <c>with</c> calls.
    /// Null if one of them can't be told apart, or is in the constructor itself.
    /// </summary>
    public static List<SyntaxNode>? ConstructorCalls(IMethodSymbol constructor, SyntaxNode scope, SyntaxNode body, SemanticModel model, CancellationToken ct)
    {
        var calls = new List<SyntaxNode>();

        foreach (var node in scope.DescendantNodes())
        {
            if (MayCreateByReflection(node, constructor.ContainingType, model, ct))
            {
                return null;
            }

            if (node is not (BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax))
            {
                continue;
            }

            var target = model.GetOperation(node, ct) switch
            {
                IObjectCreationOperation creation => creation.Constructor,
                IInvocationOperation invocation => invocation.TargetMethod,
                _ => model.GetSymbolInfo(node, ct).Symbol as IMethodSymbol is { } unknown && SymbolEqualityComparer.Default.Equals(unknown.OriginalDefinition, constructor) ? unknown : null,
            };

            if (target is null || !SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, constructor))
            {
                continue;
            }

            if (model.GetOperation(node, ct) is not (IObjectCreationOperation or IInvocationOperation)
                || (body is ConstructorDeclarationSyntax && node.SyntaxTree == body.SyntaxTree && body.Span.Contains(node.Span))
            )
            {
                return null;
            }

            calls.Add(node);
        }

        return calls;
    }

    /// <summary>
    /// Whether the node hands <paramref name="type"/> to code that could construct one by reflection, with arguments it can't see:
    /// <c>typeof(T)</c>, or T as a generic method's type argument (<c>Activator.CreateInstance&lt;T&gt;()</c>, <c>services.AddTransient&lt;T&gt;()</c>).
    /// </summary>
    private static bool MayCreateByReflection(SyntaxNode node, INamedTypeSymbol type, SemanticModel model, CancellationToken ct)
    {
        var named = node switch
        {
            TypeOfExpressionSyntax typeOf => [typeOf.Type],
            GenericNameSyntax generic when (generic.Parent is MemberAccessExpressionSyntax member && member.Name == generic ? member : (ExpressionSyntax)generic).Parent is InvocationExpressionSyntax => generic.TypeArgumentList.Arguments,
            _ => Enumerable.Empty<TypeSyntax>(),
        };

        return named.Any(typeSyntax => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(typeSyntax, ct).Type?.OriginalDefinition, type.OriginalDefinition));
    }

    /// <summary>
    /// The calls to a function, if every reference to it in <paramref name="scope"/> is a direct call from outside its own body (it's never turned into a delegate, never calls itself, and nothing calls a method of its name dynamically).
    /// Null otherwise.
    /// </summary>
    public static List<InvocationExpressionSyntax>? DirectCalls(IMethodSymbol function, SyntaxNode scope, SyntaxNode body, SemanticModel model, CancellationToken ct)
    {
        var calls = new List<InvocationExpressionSyntax>();

        foreach (var name in scope.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Identifier.ValueText != function.Name)
            {
                continue;
            }

            var callee = name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : (ExpressionSyntax)name;
            if (model.GetSymbolInfo(name, ct).Symbol is not IMethodSymbol referenced)
            {
                if (model.GetOperation(callee, ct) is IDynamicMemberReferenceOperation || model.GetOperation(callee.Parent!, ct) is IDynamicInvocationOperation)
                {
                    return null;
                }

                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(referenced.OriginalDefinition, function.OriginalDefinition))
            {
                continue;
            }

            if (callee.Parent is not InvocationExpressionSyntax call || call.Expression != callee
                || (call.SyntaxTree == body.SyntaxTree && body.Span.Contains(call.Span))
            )
            {
                return null;
            }

            calls.Add(call);
        }

        return calls;
    }

    public static SyntaxNode? EnclosingFunction(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (IsFunction(n))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the node is the boundary of a function body:
    /// a member, lambda, local function, query (whose clauses run as lambdas, possibly later), or the compilation unit (for top-level statements).
    /// </summary>
    public static bool IsFunction(SyntaxNode node) =>
        node
        is AnonymousFunctionExpressionSyntax
        or LocalFunctionStatementSyntax
        or QueryExpressionSyntax
        or CompilationUnitSyntax
        or MemberDeclarationSyntax and not GlobalStatementSyntax;

    /// <summary>
    /// Whether the node is in code that runs while an object (or a type) is constructed, where its readonly members can be written:
    /// a constructor, an <c>init</c> accessor, or a member initializer.
    /// </summary>
    public static bool IsInConstruction(SyntaxNode node) =>
        node.Ancestors().Any(a => a
            is ConstructorDeclarationSyntax
            or AccessorDeclarationSyntax { RawKind: (int)SyntaxKind.InitAccessorDeclaration }
            or EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } }
            or EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }
            or PrimaryConstructorBaseTypeSyntax
        );

    /// <summary>
    /// The members of <paramref name="type"/> its constructors and initializers (and init accessors) can call while an object (or, for its static ones, the type) is constructed, on <c>this</c> or on the type, directly or through other members of it.
    /// Null if they hand <c>this</c> (or a method of the type) to other code, call another type from a static constructor or initializer (which could call back), or go further than the analysis follows: anything could run then.
    /// </summary>
    private static HashSet<ISymbol>? ConstructionReach(INamedTypeSymbol type, bool isStatic, SemanticModel model, CancellationToken ct)
    {
        var pending = new Queue<IOperation?>(type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(ct)).OfType<TypeDeclarationSyntax>().SelectMany(declaration => declaration.Members
            .Where(m => m.Modifiers.Any(SyntaxKind.StaticKeyword) == isStatic)
            .SelectMany(m => m switch
            {
                ConstructorDeclarationSyntax constructor => [constructor],
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Initializer).OfType<SyntaxNode>(),
                PropertyDeclarationSyntax property => (property.AccessorList?.Accessors.Where(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? Enumerable.Empty<SyntaxNode>())
                    .Concat(property.Initializer is { } initializer ? [initializer] : []),
                _ => Enumerable.Empty<SyntaxNode>(),
            })
            .Concat(isStatic ? [] : declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? [])
            .Select(node => SemanticModelFor(node, model).GetOperation(node, ct))
        ));

        bool OfType(ISymbol member) =>
            SymbolEqualityComparer.Default.Equals(member.ContainingType.OriginalDefinition, type.OriginalDefinition);

        var called = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        while (pending.Count > 0)
        {
            if (pending.Dequeue() is not { } code || called.Count > Limits.ConstructionCalls)
            {
                return null;
            }

            foreach (var operation in code.DescendantsAndSelf())
            {
                var (escapes, callee) = operation switch
                {
                    IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Parent: var parent } self => parent switch
                    {
                        IFieldReferenceOperation => (false, null),
                        IInvocationOperation invocation when invocation.Instance == self => (false, invocation.TargetMethod),
                        IPropertyReferenceOperation { Property: var property } reference when reference.Instance == self => (false, Symbols.IsAutoProperty(property) ? null : property),
                        _ => (true, null),
                    },
                    IInvocationOperation { TargetMethod: { IsStatic: true } method } when isStatic && OfType(method) => (false, method),
                    IInvocationOperation { TargetMethod: var method } when isStatic && !OfType(method) => (true, null),
                    IObjectCreationOperation { Constructor: { } constructor } when isStatic && !OfType(constructor) => (true, null),
                    IMethodReferenceOperation { Method: var method } => (OfType(method), null),
                    _ => (false, (ISymbol?)null),
                };

                if (escapes)
                {
                    return null;
                }

                if (callee?.OriginalDefinition is { } definition && called.Add(definition))
                {
                    foreach (var body in definition.DeclaringSyntaxReferences.Select(r => r.GetSyntax(ct)))
                    {
                        pending.Enqueue(SemanticModelFor(body, model).GetOperation(body, ct));
                    }
                }
            }
        }

        return called;
    }

    /// <summary>
    /// Whether the code in <paramref name="function"/> (a lambda or query, which runs whenever it's invoked) could be created while the object it's in (or, in a static member, its type) is still being constructed, and so carry facts about members that aren't set for good yet:
    /// it's in a member that a base constructor could call (an override), or that construction can call (see <see cref="ConstructionReach"/>).
    /// </summary>
    public static bool MayBeCreatedDuringConstruction(SyntaxNode function, SemanticModel model, CancellationToken ct) =>
        function.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member
        || model.GetDeclaredSymbol(member, ct) is not { ContainingType: { } type } declared
        || declared.IsOverride || declared.IsVirtual || declared.IsAbstract
        || ConstructionReach(type, declared.IsStatic, model, ct) is not { } called
        || called.Contains(declared.OriginalDefinition)
        || (declared is IMethodSymbol { AssociatedSymbol: { } associated } && called.Contains(associated.OriginalDefinition));

    /// <summary>
    /// Whether an object of the type can be handed to other code (through <c>this</c>) before its constructors are done:
    /// then code anywhere can see its readonly members before they're set for good.
    /// </summary>
    public static bool ThisEscapesDuringConstruction(INamedTypeSymbol type, SemanticModel model, CancellationToken ct) =>
        ConstructionReach(type, isStatic: false, model, ct) is null;

    /// <summary>
    /// Whether a call to the method never returns:
    /// it's <c>[DoesNotReturn]</c>, or always throws (see <see cref="AlwaysThrows"/>), and isn't <c>[Conditional]</c> (which may compile the call out).
    /// </summary>
    public static bool NeverReturns(IMethodSymbol method) =>
        (Symbols.HasAttribute(method, "DoesNotReturnAttribute", "System", "Diagnostics", "CodeAnalysis") || AlwaysThrows(method))
        && !Symbols.HasAttribute(method, "ConditionalAttribute", "System", "Diagnostics");

    /// <summary>
    /// The member's declaration, if a call to it can only run that (see <see cref="CanBeDispatchedElsewhere"/>), and it's a single declaration in this compilation.
    /// </summary>
    public static SyntaxNode? OwnDeclaration(ISymbol member, Compilation compilation, CancellationToken ct = default) =>
        !CanBeDispatchedElsewhere(member)
        && member.DeclaringSyntaxReferences is { Length: 1 } references
        && references[0].GetSyntax(ct) is { } declaration
        && compilation.SyntaxTrees.Contains(declaration.SyntaxTree)
            ? declaration
            : null;

    /// <summary>
    /// The lambda <paramref name="parameter"/> belongs to, which <paramref name="node"/> is in, as the value it's passed as:
    /// through the delegate creation and conversions around it.
    /// </summary>
    public static IOperation? PassedLambda(IParameterSymbol parameter, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        var operation = model.GetOperation(node, ct);
        
        while (operation is not null && !(operation is IAnonymousFunctionOperation lambda && SymbolEqualityComparer.Default.Equals(lambda.Symbol, parameter.ContainingSymbol)))
        {
            operation = operation.Parent;
        }

        while (operation?.Parent is IDelegateCreationOperation or IConversionOperation)
        {
            operation = operation.Parent;
        }

        return operation;
    }

    /// <summary>
    /// Whether each function from <paramref name="function"/> out to (not including) <paramref name="declaringFunction"/> is a local function that runs its body when called (not async or an iterator) and is only ever called directly.
    /// </summary>
    public static bool RunsWhenCalled(SyntaxNode? function, SyntaxNode declaringFunction, SemanticModel model, CancellationToken ct)
    {
        for (; function is not null && function != declaringFunction; function = EnclosingFunction(function))
        {
            if (function is not LocalFunctionStatementSyntax local
                || model.GetDeclaredSymbol(local, ct) is not IMethodSymbol { IsAsync: false } symbol
                || ((SyntaxNode?)local.Body ?? local.ExpressionBody)?.DescendantNodes(n => !IsFunction(n)).Any(n => n is YieldStatementSyntax) != false
                || EnclosingFunction(local) is not { } scope
                || DirectCalls(symbol, scope, local, model, ct) is null
            )
            {
                return false;
            }
        }

        return function == declaringFunction;
    }

    /// <summary>
    /// The semantic model for the node's tree:
    /// <paramref name="model"/> if that's the one, or another of its compilation's.
    /// </summary>
    public static SemanticModel SemanticModelFor(SyntaxNode node, SemanticModel model) =>
        node.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(node.SyntaxTree);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Whether code writes a tracked value, or takes a reference it could be written through later.
/// Every check errs towards yes.
/// </summary>
internal static class WriteAnalysis
{
    /// <summary>
    /// Whether the node assigns (or passes by reference) one of the members the value is read through.
    /// </summary>
    private static bool AssignsMember(SyntaxNode node, TrackedValue value, SemanticModel model, CancellationToken ct)
    {
        var members = value.MembersReadThrough.Select(m => m.OriginalDefinition).ToList();
        foreach (var root in OperationsIn(node, model, ct))
        {
            if (root is null)
            {
                return true;
            }

            foreach (var operation in root.DescendantsAndSelf())
            {
                var targets = operation switch
                {
                    IAssignmentOperation assignment => assignment.Target.DescendantsAndSelf(),
                    IIncrementOrDecrementOperation increment => increment.Target.DescendantsAndSelf(),
                    IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument => argument.Value.DescendantsAndSelf(),
                    _ => [],
                };

                if (targets.Any(target => target is IMemberReferenceOperation reference && members.Contains(reference.Member.OriginalDefinition, SymbolEqualityComparer.Default)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a reference passed to the parameter can outlive the call and be written through.
    /// </summary>
    private static bool CanEscape(IParameterSymbol parameter) =>
        parameter.RefKind == RefKind.Ref && parameter.ScopedKind != ScopedKind.ScopedRef;

    /// <summary>
    /// Whether a reference passed as the argument can outlive the call and be written through:
    /// the parameter lets it escape, or the call hands back a reference, or a ref struct that can hold one (<c>ref Unsafe.AsRef(in x)</c>, <c>MemoryMarshal.CreateSpan(ref x, 1)</c>), which could be to it whatever the parameter promises.
    /// (A dynamic call has no parameter to look at.)
    /// </summary>
    private static bool CanEscape(IArgumentOperation argument) =>
        argument.Parameter is not { } parameter
        || CanEscape(parameter)
        || (parameter.RefKind is not (RefKind.None or RefKind.Out) && argument.Parent switch
        {
            IInvocationOperation { TargetMethod: var method } => method.ReturnsByRef || method.ReturnsByRefReadonly || method.ReturnType.IsRefLikeType,
            IObjectCreationOperation { Type.IsRefLikeType: true } => true,
            _ => false,
        });

    /// <summary>
    /// Whether the member, called on a struct variable, can change it:
    /// it isn't readonly, and isn't one that only reads <c>this</c> anyway (see <see cref="OnlyReadsThis"/>).
    /// </summary>
    private static bool CanChangeStruct(IMethodSymbol? member, Compilation compilation, CancellationToken ct) =>
        member is { IsReadOnly: false, IsStatic: false, ContainingType.IsValueType: true } && !OnlyReadsThis(member, compilation, ct);

    /// <summary>
    /// Whether matching the pattern against a struct variable calls a member of the variable itself (not a copy) that can change it:
    /// a Deconstruct, a property subpattern's getter, a list pattern's Length, Count or indexer, or any of those on a field of it.
    /// </summary>
    private static bool PatternCanChangeInput(IPatternOperation pattern, Compilation compilation, CancellationToken ct) => pattern switch
    {
        IRecursivePatternOperation recursive =>
            CanChangeStruct(recursive.DeconstructSymbol as IMethodSymbol, compilation, ct)
            || recursive.PropertySubpatterns.Any(subpattern => subpattern.Member switch
            {
                IPropertyReferenceOperation { Property: var property } => CanChangeStruct(property.GetMethod, compilation, ct),
                IFieldReferenceOperation { Field.Type.IsValueType: true } => PatternCanChangeInput(subpattern.Pattern, compilation, ct),
                _ => false,
            }),
        IListPatternOperation list => new[] { list.LengthSymbol, list.IndexerSymbol }.Any(member => CanChangeStruct((member as IPropertySymbol)?.GetMethod ?? member as IMethodSymbol, compilation, ct)),
        INegatedPatternOperation not => PatternCanChangeInput(not.Pattern, compilation, ct),
        IBinaryPatternOperation binary => PatternCanChangeInput(binary.LeftPattern, compilation, ct) || PatternCanChangeInput(binary.RightPattern, compilation, ct),
        _ => false,
    };

    /// <summary>
    /// What data flow says about the statement, expression or <c>when</c> clause, or null if it can't be worked out there.
    /// </summary>
    private static DataFlowAnalysis? DataFlow(SyntaxNode node, SemanticModel model)
    {
        try
        {
            var flow = node switch
            {
                StatementSyntax statement => model.AnalyzeDataFlow(statement),
                ExpressionSyntax expression => model.AnalyzeDataFlow(expression),
                WhenClauseSyntax whenClause => model.AnalyzeDataFlow(whenClause.Condition),
                _ => null,
            };

            return flow is { Succeeded: true } ? flow : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// For a value that something else could change (see <see cref="TrackedValue.IsExternallyMutable"/>):
    /// whether the node runs anything that could, or writes it directly.
    /// Anything that runs code the analysis doesn't see counts:
    /// calls, constructors (<c>new T()</c> too), property getters other than auto-properties' (and indexers), user-defined operators and conversions, string interpolation, <c>await</c>, <c>yield</c>, <c>foreach</c>, <c>using</c>;
    /// and code that runs without a call: a Deconstruct written by hand (deconstructing, or a positional pattern), a list pattern's Length and indexer, a collection expression's builder or Add and a spread's enumerator, a record's own copy constructor (<c>with</c>), the <c>==</c> of a tuple's elements, a ToString in string concatenation, and another type's static constructor or initializers, which the first use of one of its static members runs;
    /// so does taking a reference, and assigning a member the value is read through, any array element, or <c>this</c>.
    /// </summary>
    private static bool MayChange(SyntaxNode node, TrackedValue value, SemanticModel model, int? before, CancellationToken ct)
    {
        if (node.DescendantNodesAndSelf().Any(n =>
            (n is YieldStatementSyntax or AwaitExpressionSyntax or CommonForEachStatementSyntax or UsingStatementSyntax|| n is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
            && (before is null || n.Span.End <= before)
        ))
        {
            return true;
        }

        var members = value.MembersReadThrough.Select(m => m.OriginalDefinition).ToList();
        var followedTypes = members.Select(m => m.ContainingType).ToList();
        foreach (var root in OperationsIn(node, model, ct))
        {
            if (root is null)
            {
                return true;
            }

            foreach (var operation in root.DescendantsAndSelf())
            {
                if (before is { } end && operation.Syntax.Span.End > end)
                {
                    continue;
                }

                switch (operation)
                {
                    case IWithOperation with when !Functions.CopiesAsIs(with):
                    case ICollectionExpressionOperation { ConstructMethod: not null }:
                    case ISpreadOperation:
                    case ITypeParameterObjectCreationOperation:
                    case IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol { IsImplicitlyDeclared: false } }:
                    case IListPatternOperation { InputType: not (IArrayTypeSymbol or { SpecialType: SpecialType.System_String }) }:
                    case IDeconstructionAssignmentOperation { Syntax: AssignmentExpressionSyntax deconstruction } when CallsDeconstructWrittenByHand(model.GetDeconstructionInfo(deconstruction)):
                    case ITupleBinaryOperation { LeftOperand: var tuple } when tuple.Type is INamedTypeSymbol { IsTupleType: true } tupleType && tupleType.TupleElements.Any(e => OperatorsMayRunUserCode(e.Type)):
                    case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null, Type.SpecialType: SpecialType.System_String } concatenation
                        when OperatorsMayRunUserCode(Unconverted(concatenation.LeftOperand).Type) || OperatorsMayRunUserCode(Unconverted(concatenation.RightOperand).Type):
                    case IMemberReferenceOperation { Member: { IsStatic: true, ContainingType: { } owner } and not IFieldSymbol { IsConst: true } }
                        when !followedTypes.Contains(owner.OriginalDefinition, SymbolEqualityComparer.Default) && HasTypeInitializer(owner):
                    case IInvocationOperation:
                    case IFunctionPointerInvocationOperation:
                    case IImplicitIndexerReferenceOperation:
                    case IObjectCreationOperation { Constructor: not { IsImplicitlyDeclared: true, ContainingType.IsUnion: true } }:
                    case IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation:
                    case IEventAssignmentOperation:
                    case IInterpolatedStringOperation:
                    case IConversionOperation { OperatorMethod: { } conversion } when !(conversion is { MethodKind: MethodKind.Constructor, ContainingType.IsUnion: true }):
                    case IBinaryOperation { OperatorMethod: not null }:
                    case IUnaryOperation { OperatorMethod: not null }:
                    case IIncrementOrDecrementOperation { OperatorMethod: not null }:
                    case ICompoundAssignmentOperation { OperatorMethod: not null }:
                    case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }:
                    case IPropertyReferenceOperation { Property: var property } when property.IsIndexer
                        ? !Symbols.IsImmutableArray(property.ContainingType)
                        : property.IsVirtual || property.IsAbstract || property.IsOverride || !Symbols.IsAutoProperty(property):
                        return true;

                    case IVariableDeclaratorOperation { Symbol.IsRef: true }:
                        return true;

                    case IAssignmentOperation or IIncrementOrDecrementOperation or IDeconstructionAssignmentOperation:
                        var targets = operation switch
                        {
                            IAssignmentOperation assignment => assignment.Target.DescendantsAndSelf(),
                            IIncrementOrDecrementOperation increment => increment.Target.DescendantsAndSelf(),
                            _ => ((IDeconstructionAssignmentOperation)operation).Target.DescendantsAndSelf(),
                        };

                        if (targets.Any(target => target is IArrayElementReferenceOperation or IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type.IsValueType: true }
                            || target is IMemberReferenceOperation reference && members.Contains(reference.Member.OriginalDefinition, SymbolEqualityComparer.Default)
                        ))
                        {
                            return true;
                        }

                        break;
                }
            }
        }

        return false;
    }

    private static bool CallsDeconstructWrittenByHand(DeconstructionInfo info) =>
        info.Method is { IsImplicitlyDeclared: false } || info.Nested.Any(CallsDeconstructWrittenByHand);

    /// <summary>
    /// Whether the type, declared in the compilation, has a static constructor or static initializers, which run when one of its static members is first used.
    /// </summary>
    private static bool HasTypeInitializer(INamedTypeSymbol type) =>
        type.StaticConstructors.Any(constructor => !constructor.IsImplicitlyDeclared)
        || type.GetMembers().Any(member => member.IsStatic && member.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } or PropertyDeclarationSyntax { Initializer: not null }));

    /// <summary>
    /// Whether comparing a value of the type with <c>==</c>, or turning it into text, can run code the program declares (a user-defined operator, a ToString override):
    /// it's not a built-in type or an enum.
    /// </summary>
    private static bool OperatorsMayRunUserCode(ITypeSymbol? type) =>
        type is not null and not { SpecialType: not SpecialType.None } and not { TypeKind: TypeKind.Enum };

    private static IOperation Unconverted(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>
    /// Data flow counts calling any member of a struct that isn't marked readonly as writing the struct.
    /// Whether that's all the node does to the variable, with every such member one that only reads <c>this</c> (see <see cref="OnlyReadsThis"/>):
    /// nothing else writes it, and it isn't declared there.
    /// </summary>
    private static bool OnlyCallsMembersThatReadThis(SyntaxNode node, ISymbol root, SemanticModel model, CancellationToken ct)
    {
        if (root is not (ILocalSymbol { Type.IsValueType: true } or IParameterSymbol { Type.IsValueType: true }) || root.DeclaringSyntaxReferences.Any(reference => node.Span.Contains(reference.Span)))
        {
            return false;
        }

        var uses = Symbols.References(root, node, model, ct).ToList();
        return
            uses.Any(use => model.GetOperation(use, ct) is { } operation && operation.Parent switch
                {
                    IInvocationOperation invocation when invocation.Instance == operation => !invocation.TargetMethod.IsReadOnly,
                    IPropertyReferenceOperation property when property.Instance == operation => property.Property.GetMethod is not { IsReadOnly: true },
                    _ => false,
                }
            )
            && !uses.Any(use => IsWrite(use, model, ct));
    }

    /// <summary>
    /// The operations the node is made of:
    /// its own, a <c>when</c> clause's condition's, or a switch section's statements'.
    /// A null among them is a part that has no operation.
    /// </summary>
    private static IEnumerable<IOperation?> OperationsIn(SyntaxNode node, SemanticModel model, CancellationToken ct) => node switch
    {
        WhenClauseSyntax whenClause => [model.GetOperation(whenClause.Condition, ct)],
        SwitchSectionSyntax section => section.Statements.Select(statement => model.GetOperation(statement, ct)),
        _ => [model.GetOperation(node, ct)],
    };

    private static bool ReceiverCanEscape(ISymbol member)
    {
        static bool HasUnscopedRef(ISymbol? symbol) =>
            symbol is not null && Symbols.HasAttribute(symbol, "UnscopedRefAttribute", "System", "Diagnostics", "CodeAnalysis");

        if (member.ContainingType is { IsExtension: true, ExtensionParameter: { } receiver } && CanEscape(receiver))
        {
            return true;
        }

        return HasUnscopedRef(member) || (member is IPropertySymbol property && (HasUnscopedRef(property.GetMethod) || HasUnscopedRef(property.SetMethod)));
    }

    /// <summary>
    /// The variable with the struct fields read from it (<c>t.Item1</c>, <c>box.Inner.Pet</c>):
    /// writing one of those, or taking a reference to one, writes the variable, or takes a reference into it.
    /// </summary>
    private static ExpressionSyntax StructFieldChain(IdentifierNameSyntax name, SemanticModel model, CancellationToken ct)
    {
        ExpressionSyntax expression = name;
        while (true)
        {
            var outer = expression;
            while (outer.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                outer = parenthesized;
            }

            if (outer.Parent is MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member
                && member.Expression == outer
                && model.GetOperation(member, ct) is IFieldReferenceOperation { Field.IsStatic: false, Instance.Type.IsValueType: true }
            )
            {
                expression = member;
                continue;
            }

            return expression;
        }
    }

    /// <summary>
    /// <c>this._pet = ...</c>, <c>M(out this._pet)</c>, <c>ref Owner.Favourite</c>, ...:
    /// the name read through a member access, written.
    /// </summary>
    public static bool IsTargetOfMemberWrite(IdentifierNameSyntax name)
    {
        if (name.Parent is not MemberAccessExpressionSyntax access || access.Name != name)
        {
            return false;
        }

        ExpressionSyntax target = access;
        while (target.Parent is ParenthesizedExpressionSyntax or TupleExpressionSyntax || target.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax })
        {
            target = target.Parent is ArgumentSyntax argument ? (TupleExpressionSyntax)argument.Parent! : (ExpressionSyntax)target.Parent!;
        }

        return target.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == target,
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None) && !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword),
            RefExpressionSyntax => true,
            PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) || prefix.IsKind(SyntaxKind.AddressOfExpression),
            PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
            _ => false,
        };
    }

    /// <summary>
    /// Whether this use of the variable writes it:
    /// as the target of an assignment (or deconstruction), an increment, an <c>out</c> argument, or the receiver of a call that can change a struct.
    /// </summary>
    public static bool IsWrite(IdentifierNameSyntax name, SemanticModel model, CancellationToken ct)
    {
        ExpressionSyntax expression = StructFieldChain(name, model, ct);
        while (true)
        {
            while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
            }

            if (expression.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax tuple }) // (a, b) = ...
            {
                expression = tuple;
            }
            else
            {
                break;
            }
        }

        switch (expression.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == expression:
            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
            case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
                return true;
        }

        // A struct's members get `this` by reference, and only readonly ones promise not to change it.
        if (model.GetOperation(expression, ct) is not { Type.IsValueType: true } operation)
        {
            return false;
        }

        return operation.Parent switch
        {
            IInvocationOperation invocation when invocation.Instance == operation =>
                SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, operation.Type) && CanChangeStruct(invocation.TargetMethod, model.Compilation, ct),
            IPropertyReferenceOperation property when property.Instance == operation =>
                IsAssigned(property) || (property.Property.GetMethod is not { IsReadOnly: true } && !OnlyReadsThis(property.Property.GetMethod, model.Compilation, ct)),
            IFieldReferenceOperation field when field.Instance == operation => IsAssigned(field),
            IMemberReferenceOperation member when member.Instance == operation => true,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => true, // the receiver of a `this ref` extension method
            IIsPatternOperation isPattern when isPattern.Value == operation => PatternCanChangeInput(isPattern.Pattern, model.Compilation, ct),
            ISwitchExpressionOperation switchExpression when switchExpression.Value == operation =>
                switchExpression.Arms.Any(arm => PatternCanChangeInput(arm.Pattern, model.Compilation, ct)),
            ISwitchOperation switchStatement when switchStatement.Value == operation =>
                switchStatement.Cases.SelectMany(c => c.Clauses).OfType<IPatternCaseClauseOperation>().Any(clause => PatternCanChangeInput(clause.Pattern, model.Compilation, ct)),
            _ => false,
        };

        // Assigned (plainly, compound, or by ??=), incremented, passed by reference, or deconstructed into.
        static bool IsAssigned(IOperation reference)
        {
            if (reference.Parent is IAssignmentOperation assignment && assignment.Target == reference
                || reference.Parent is IIncrementOrDecrementOperation increment && increment.Target == reference
                || reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref }
            )
            {
                return true;
            }

            var target = reference;
            while (target.Parent is ITupleOperation tuple)
            {
                target = tuple;
            }

            return target != reference && target.Parent is IDeconstructionAssignmentOperation deconstruction && deconstruction.Target == target;
        }
    }

    /// <summary>
    /// Whether a struct member that isn't marked readonly still can't change <c>this</c>, going by its source:
    /// nothing in its body assigns <c>this</c> or a field of it, passes one by reference, makes a lambda or local function, or calls a member of one that isn't readonly.
    /// (A union's own <c>bool IsBird =&gt; this is Bird;</c>, say, or a <c>bool TryGetCat(out Cat cat)</c>, which only assigns its parameter.)
    /// </summary>
    public static bool OnlyReadsThis(IMethodSymbol? method, Compilation compilation, CancellationToken ct = default)
    {
        if (method is null || method.DeclaringSyntaxReferences.Length != 1)
        {
            return false;
        }

        SyntaxNode? code = method.DeclaringSyntaxReferences[0].GetSyntax(ct) switch
        {
            MethodDeclarationSyntax declaration => (SyntaxNode?)declaration.ExpressionBody?.Expression ?? declaration.Body,
            AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.ExpressionBody?.Expression ?? accessor.Body,
            ArrowExpressionClauseSyntax arrow => arrow.Expression, // an expression-bodied property's getter
            _ => null,
        };

        if (code is null || !compilation.SyntaxTrees.Contains(code.SyntaxTree) || compilation.GetSemanticModel(code.SyntaxTree).GetOperation(code, ct) is not { } body)
        {
            return false;
        }

        return !body.DescendantsAndSelf().Any(operation => operation switch
        {
            IAssignmentOperation { Target: var assigned } => assigned.DescendantsAndSelf().Any(IsThisOrItsStructField),
            IIncrementOrDecrementOperation { Target: var incremented } => incremented.DescendantsAndSelf().Any(IsThisOrItsStructField),
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out, Value: var passed } => IsThisOrItsStructField(passed),
            IAnonymousFunctionOperation or ILocalFunctionOperation => true,
            IVariableDeclaratorOperation { Symbol.IsRef: true } => true,
            IInvocationOperation { Instance: var instance, TargetMethod.IsReadOnly: false } when IsThisOrItsStructField(instance) => true,
            IPropertyReferenceOperation { Instance: var instance, Property.GetMethod: not { IsReadOnly: true } } when IsThisOrItsStructField(instance) => true,
            _ => false,
        });

        // `this`, or a struct field of it: what's done to one of these is done to the storage of `this` itself.
        static bool IsThisOrItsStructField(IOperation? operation) => operation switch
        {
            IInstanceReferenceOperation => true,
            IFieldReferenceOperation { Field: { IsStatic: false, Type.IsValueType: true }, Instance: var outer } => IsThisOrItsStructField(outer),
            _ => false,
        };
    }

    /// <summary>
    /// Whether this use of the variable takes a reference to it that could be written through later.
    /// <c>out</c>, <c>scoped ref</c> and read-only (<c>in</c>, <c>ref readonly</c>) arguments don't:
    /// <c>out</c> and <c>scoped ref</c> parameters can't escape the call, and a write at the call itself is caught as a write.
    /// </summary>
    public static bool TakesReference(IdentifierNameSyntax name, SemanticModel model, CancellationToken ct)
    {
        ExpressionSyntax expression = StructFieldChain(name, model, ct);
        while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized;
        }

        switch (expression.Parent)
        {
            case RefExpressionSyntax:                                                                   // ref Pet alias = ref pet;
            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.AddressOfExpression): // &pet
            case MakeRefExpressionSyntax:                                                               // __makeref(pet)
                return true;

            // M(ref pet), unless M's parameter is `scoped ref`.
            case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
                return model.GetOperation(argument, ct) is not IArgumentOperation passed || CanEscape(passed);
        }

        // References passed without a `ref` keyword: `this ref` extension methods, `ref` extension blocks, and
        // [UnscopedRef] members, which can hand back a reference to `this`.
        var operation = model.GetOperation(expression, ct);
        return operation?.Parent switch
        {
            IArgumentOperation argument => CanEscape(argument),
            IInvocationOperation invocation when invocation.Instance == operation => ReceiverCanEscape(invocation.TargetMethod),
            IMemberReferenceOperation member when member.Instance == operation => ReceiverCanEscape(member.Member),
            _ => false,
        };
    }

    /// <summary>
    /// The expression inside any parentheses, which is what has an operation (and data flow) of its own.
    /// </summary>
    public static ExpressionSyntax WithoutParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>
    /// Whether the statement or expression writes the value anywhere.
    /// </summary>
    public static bool Writes(SyntaxNode node, TrackedValue value, SemanticModel model, CancellationToken ct = default) =>
        Writes(node is ExpressionSyntax expression ? WithoutParentheses(expression) : node, value, model, before: null, ct);

    /// <param name="before">For a value that something else could change (<see cref="TrackedValue.IsExternallyMutable"/>), only code that ends before this position counts as possibly changing it (the rest runs after it's read).</param>
    public static bool Writes(SyntaxNode node, TrackedValue value, SemanticModel model, int? before, CancellationToken ct)
    {
        if (value.IsExternallyMutable && MayChange(node, value, model, before, ct))
        {
            return true;
        }

        if (value.IndexVariables.Any(index => DataFlow(node, model) is not { } flow || flow.WrittenInside.Contains(index, SymbolEqualityComparer.Default)))
        {
            return true;
        }

        // A chain from `this` or a static member can't be written outside construction (see IsTrackable), and in a
        // constructor only by assigning one of its members.
        if (value.Variable is not { } variable)
        {
            return !value.Steps.IsEmpty && Functions.IsInConstruction(node) && AssignsMember(node, value, model, ct);
        }

        // Data flow can't be worked out for some parts of a condition (`a && b` as an operand of `||`), but it can for
        // their operands.
        if (DataFlow(node, model) is not { } dataFlow)
        {
            return node is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression or (int)SyntaxKind.LogicalOrExpression } logical
                || Writes(WithoutParentheses(logical.Left), value, model, before, ct) || Writes(WithoutParentheses(logical.Right), value, model, before, ct);
        }

        if (dataFlow.WrittenInside.Contains(variable, SymbolEqualityComparer.Default))
        {
            return !OnlyCallsMembersThatReadThis(node, variable, model, ct);
        }

        // A member of a struct variable can be written (a field assigned, a setter called) without data flow counting
        // it as a write of the variable.
        return !value.Steps.IsEmpty
            && variable is ILocalSymbol { Type.IsValueType: true } or IParameterSymbol { Type.IsValueType: true }
            && Symbols.References(variable, node, model, ct).Any(name => IsWrite(name, model, ct));
    }
}

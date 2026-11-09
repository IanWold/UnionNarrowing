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
    /// calls, constructors, property getters other than auto-properties' (and indexers), user-defined operators and conversions, string interpolation, <c>await</c>, <c>yield</c>, <c>foreach</c>, <c>using</c>;
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
                    case IInvocationOperation:
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
                !invocation.TargetMethod.IsReadOnly && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, operation.Type)
                && !OnlyReadsThis(invocation.TargetMethod, model.Compilation, ct),
            IPropertyReferenceOperation property when property.Instance == operation =>
                IsAssigned(property) || (property.Property.GetMethod is not { IsReadOnly: true } && !OnlyReadsThis(property.Property.GetMethod, model.Compilation, ct)),
            IFieldReferenceOperation field when field.Instance == operation => IsAssigned(field),
            IMemberReferenceOperation member when member.Instance == operation => true,
            _ => false,
        };

        static bool IsAssigned(IOperation reference) =>
            reference.Parent is IAssignmentOperation assignment && assignment.Target == reference
            || reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref };
    }

    /// <summary>
    /// Whether a struct member that isn't marked readonly still can't change <c>this</c>, going by its source:
    /// its body is a single expression that assigns nothing, passes nothing by reference, and calls only readonly members of <c>this</c>.
    /// (A union's own <c>bool IsBird =&gt; this is Bird;</c>, say.)
    /// </summary>
    public static bool OnlyReadsThis(IMethodSymbol? method, Compilation compilation, CancellationToken ct = default)
    {
        if (method is null || method.DeclaringSyntaxReferences.Length != 1)
        {
            return false;
        }

        var expression = method.DeclaringSyntaxReferences[0].GetSyntax(ct) switch
        {
            MethodDeclarationSyntax declaration => declaration.ExpressionBody?.Expression,
            AccessorDeclarationSyntax accessor => accessor.ExpressionBody?.Expression,
            ArrowExpressionClauseSyntax arrow => arrow.Expression, // an expression-bodied property's getter
            _ => null,
        };

        if (expression is null || !compilation.SyntaxTrees.Contains(expression.SyntaxTree) || compilation.GetSemanticModel(expression.SyntaxTree).GetOperation(expression, ct) is not { } body)
        {
            return false;
        }

        return !body.DescendantsAndSelf().Any(operation => operation switch
        {
            IAssignmentOperation or IIncrementOrDecrementOperation or IDeconstructionAssignmentOperation => true,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => true,
            IAnonymousFunctionOperation or ILocalFunctionOperation => true,
            IInvocationOperation { Instance: IInstanceReferenceOperation, TargetMethod.IsReadOnly: false } => true,
            IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property.GetMethod: not { IsReadOnly: true } } => true,
            _ => false,
        });
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

            // M(ref pet), unless M's parameter is `scoped ref`. (A dynamic call has no parameter to look at.)
            case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
                return model.GetOperation(argument, ct) is not IArgumentOperation { Parameter: { } parameter } || CanEscape(parameter);
        }

        // References passed without a `ref` keyword: `this ref` extension methods, `ref` extension blocks, and
        // [UnscopedRef] members, which can hand back a reference to `this`.
        var operation = model.GetOperation(expression, ct);
        return operation?.Parent switch
        {
            IArgumentOperation argument => argument.Parameter is not { } parameter || CanEscape(parameter),
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

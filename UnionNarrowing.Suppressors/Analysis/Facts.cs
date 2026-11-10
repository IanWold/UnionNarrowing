using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What patterns and conditions say about which case a value is in.
/// Anything not understood is <see cref="Outcome.Unknown"/>, which keeps the analysis conservative.
/// </summary>
internal static class Facts
{
    /// <summary>
    /// Whether every non-null value of <paramref name="caseType"/> (or of any other type) is a <paramref name="type"/>.
    /// </summary>
    private static bool AlwaysInstanceOf(ITypeSymbol caseType, ITypeSymbol type, CSharpCompilation compilation)
    {
        if (Symbols.UnderlyingOfNullable(caseType) is { } underlying)
        {
            return AlwaysInstanceOf(underlying, type, compilation);
        }

        var conversion = compilation.ClassifyConversion(caseType, type);
        return conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing));
    }

    /// <summary>
    /// Whether a pattern's input is a union other than the one the space is of:
    /// one a case holds.
    /// (In generic code read without knowing what its type parameters stand for, the same union with other type arguments might be this one.)
    /// </summary>
    private static bool AppliesToAnotherUnion(ITypeSymbol input, CaseSpace space, Compilation compilation)
    {
        var inner = space.Interpret(Symbols.UnderlyingOfNullable(input) ?? input, compilation);

        return inner.IsUnion
            && !SymbolEqualityComparer.Default.Equals(inner, space.UnionType)
            && !(TypeArguments.Mentioned(inner) && SymbolEqualityComparer.Default.Equals(inner.OriginalDefinition, space.UnionType?.OriginalDefinition));
    }

    /// <summary>
    /// For <c>e == true</c>, <c>false != e</c>, ...
    /// on bools: e, and the constant.
    /// </summary>
    private static (IOperation Other, bool Constant)? BoolComparedWithConstant(IBinaryOperation equality)
    {
        if (!IsLogical(equality))
        {
            return null;
        }

        if (equality.RightOperand.ConstantValue is { HasValue: true, Value: bool right } && !equality.LeftOperand.ConstantValue.HasValue)
        {
            return (equality.LeftOperand, right);
        }

        if (equality.LeftOperand.ConstantValue is { HasValue: true, Value: bool left } && !equality.RightOperand.ConstantValue.HasValue)
        {
            return (equality.RightOperand, left);
        }

        return null;
    }

    /// <summary>
    /// Whether some non-null value of <paramref name="caseType"/> could be a <paramref name="type"/>.
    /// Errs towards yes.
    /// </summary>
    private static bool CanBeInstanceOf(ITypeSymbol caseType, ITypeSymbol type, CSharpCompilation compilation)
    {
        if (caseType is ITypeParameterSymbol || type is ITypeParameterSymbol)
        {
            return true;
        }

        if (Symbols.UnderlyingOfNullable(caseType) is { } underlying)
        {
            return CanBeInstanceOf(underlying, type, compilation);
        }

        // A struct, enum or sealed class value is exactly that type at run time, so the static relationship decides.
        // (Not delegates or arrays: variance lets a Func<Cat> hold a Func<Kitten>, and an Animal[] a Dog[].) Where
        // either type is made from type parameters, some type arguments could make them the same type, or one a base
        // type or interface of the other.
        var exactly = caseType.TypeKind is TypeKind.Struct or TypeKind.Enum || (caseType.TypeKind == TypeKind.Class && caseType.IsSealed);
        if (TypeArguments.Mentioned(caseType) || TypeArguments.Mentioned(type))
        {
            return !exactly || SelfAndBaseTypes(caseType).Any(t => CouldBeSameType(t, type) || IsVariantOfSameDefinition(t, type));
        }

        if (exactly)
        {
            return AlwaysInstanceOf(caseType, type, compilation);
        }

        // Otherwise a derived type might be one. C# allows at least an explicit conversion between any two types a
        // value could be both of, so no conversion at all means no value can be both.
        return compilation.ClassifyConversion(caseType, type).Exists;
    }

    /// <summary>
    /// Whether <c>value op bound</c> holds, for the relational operators.
    /// </summary>
    private static bool? Compares(BinaryOperatorKind kind, decimal value, decimal bound) => kind switch
    {
        BinaryOperatorKind.LessThan => value < bound,
        BinaryOperatorKind.LessThanOrEqual => value <= bound,
        BinaryOperatorKind.GreaterThan => value > bound,
        BinaryOperatorKind.GreaterThanOrEqual => value >= bound,
        _ => null,
    };

    /// <summary>
    /// The constant a sealed type's implementation of the property returns, if its getter does nothing else:
    /// <c>public override ShapeKind Kind =&gt; ShapeKind.Circle;</c>.
    /// </summary>
    private static Optional<object?> ConstantOf(IPropertySymbol property, ITypeSymbol type, Compilation compilation)
    {
        static bool Implements(IPropertySymbol candidate, IPropertySymbol property)
        {
            for (var overridden = candidate; overridden is not null; overridden = overridden.OverriddenProperty)
            {
                if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, property.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        if (!type.IsSealed && !type.IsValueType)
        {
            return default;
        }

        for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            var implementation = declaring.GetMembers(property.Name).OfType<IPropertySymbol>().FirstOrDefault(p => Implements(p, property));
            if (implementation is null)
            {
                continue;
            }

            return implementation.DeclaringSyntaxReferences.Length == 1
                && HelperBody(implementation.DeclaringSyntaxReferences[0].GetSyntax()) is var body
                && (body as ExpressionSyntax ?? (body is BlockSyntax { Statements.Count: 1 } block && block.Statements[0] is ReturnStatementSyntax { Expression: { } returned } ? returned : null)) is { } constant
                && compilation.SyntaxTrees.Contains(constant.SyntaxTree)
                && compilation.GetSemanticModel(constant.SyntaxTree) is var model
                && SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(constant).Type, property.Type)
                    ? model.GetConstantValue(constant)
                    : default;
        }

        return default;
    }

    /// <summary>
    /// For a property every case of a closed hierarchy gives a constant for (a discriminant: <c>Kind</c>, with each sealed leaf overriding <c>Kind =&gt; ShapeKind.Circle</c>):
    /// what a test of it says about the value, true for the cases whose constant <paramref name="passes"/>, false for those whose constant fails it.
    /// Null if no case gives one.
    /// </summary>
    private static Outcome? OfDiscriminant(IPropertySymbol property, Func<object?, bool?> passes, CaseSpace space, Compilation compilation)
    {
        // Only types whose constants compare exactly: not floating point (NaN), decimal (1.0m is 1m), or object (boxed).
        if (space.Kind != SpaceKind.Closed
            || property.Type is not ({ TypeKind: TypeKind.Enum } or { SpecialType: SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_String
                or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            })
        )
        {
            return null;
        }

        var (alwaysTrue, alwaysFalse, known) = (new List<object>(), new List<object>(), false);
        foreach (var candidate in space.Cases.OfType<ITypeSymbol>())
        {
            if (ConstantOf(property, candidate, compilation) is { HasValue: true } constant)
            {
                known = true;
                if (passes(constant.Value) is bool holds)
                {
                    (holds ? alwaysTrue : alwaysFalse).Add(candidate);
                }
            }
        }

        return known ? new Outcome(CaseSet.Of(alwaysTrue), CaseSet.Of(alwaysFalse)) : null;
    }

    /// <summary>
    /// <see cref="OfDiscriminant(IPropertySymbol, Func{object?, bool?}, CaseSpace, Compilation)"/> for a read of a discriminant of the value (<c>shape.Kind</c>), as it is: one converted first (<c>shape.Sides == 4.0</c>) is compared as something else.
    /// </summary>
    private static Outcome? OfDiscriminant(IOperation read, Func<object?, bool?> passes, TrackedValue value, CaseSpace space, Compilation compilation) =>
        read is IPropertyReferenceOperation { Instance: { } instance, Property: var property, Arguments.IsEmpty: true } && Tests(instance, value, space)
            ? OfDiscriminant(property, passes, space, compilation)
            : null;

    /// <summary>
    /// Whether a pattern matches a constant:
    /// null where that can't be told.
    /// </summary>
    private static bool? PatternHolds(IPatternOperation pattern, object? constant) => pattern switch
    {
        IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => true,
        IConstantPatternOperation { Value.ConstantValue: { HasValue: true } matched } => Equals(CaseSpace.ConstantKey(matched.Value), CaseSpace.ConstantKey(constant)),
        IRelationalPatternOperation { Value.ConstantValue: { HasValue: true } bound } relational => CaseSpace.ConstantKey(constant) is decimal value && CaseSpace.ConstantKey(bound.Value) is decimal limit ? Compares(relational.OperatorKind, value, limit) : null,
        INegatedPatternOperation not => !PatternHolds(not.Pattern, constant),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => PatternHolds(or.LeftPattern, constant) is var left && left == true ? true : left == false ? PatternHolds(or.RightPattern, constant) : PatternHolds(or.RightPattern, constant) == true ? true : null,
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => PatternHolds(and.LeftPattern, constant) is var left && left == false ? false : left == true ? PatternHolds(and.RightPattern, constant) : PatternHolds(and.RightPattern, constant) == false ? false : null,
        _ => null,
    };

    /// <summary>
    /// Whether the operator is a record's generated <c>==</c> or <c>!=</c>, over Equals methods (its own and its base records') that are all generated too:
    /// comparing one with null then says only whether it is null.
    /// </summary>
    private static bool IsGeneratedRecordEquality(IMethodSymbol operatorMethod)
    {
        if (operatorMethod is not { IsImplicitlyDeclared: true, ContainingType.IsRecord: true })
        {
            return false;
        }

        for (var record = operatorMethod.ContainingType; record is { IsRecord: true }; record = record.BaseType)
        {
            if (record.GetMembers("Equals").Any(equals => !equals.IsImplicitlyDeclared))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether some type arguments for the type parameters the types are made from could make them the same type:
    /// <c>Some&lt;T&gt;</c> and <c>Some&lt;int&gt;</c> could be, <c>Some&lt;T&gt;</c> and <c>None</c> can't.
    /// </summary>
    private static bool CouldBeSameType(ITypeSymbol left, ITypeSymbol right) => (left, right) switch
    {
        (ITypeParameterSymbol, _) or (_, ITypeParameterSymbol) => true,
        (IArrayTypeSymbol leftArray, IArrayTypeSymbol rightArray) => leftArray.Rank == rightArray.Rank && CouldBeSameType(leftArray.ElementType, rightArray.ElementType),
        (INamedTypeSymbol leftNamed, INamedTypeSymbol rightNamed) when SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition) =>
            leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, CouldBeSameType).All(could => could)
            && (leftNamed.ContainingType is null || CouldBeSameType(leftNamed.ContainingType, rightNamed.ContainingType!)),
        _ => SymbolEqualityComparer.Default.Equals(left, right),
    };

    /// <summary>
    /// Whether both are the same variant interface or delegate (<c>IEnumerable&lt;out T&gt;</c>), whatever their type arguments, which variance can convert between.
    /// </summary>
    private static bool IsVariantOfSameDefinition(ITypeSymbol left, ITypeSymbol right) =>
        left is INamedTypeSymbol { OriginalDefinition: var definition }
        && SymbolEqualityComparer.Default.Equals(definition, right.OriginalDefinition)
        && definition.TypeParameters.Any(parameter => parameter.Variance != VarianceKind.None);

    private static IEnumerable<ITypeSymbol> SelfAndBaseTypes(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            yield return implemented;
        }
    }

    /// <summary>
    /// For <c>x == c</c> or <c>c == x</c>, where <c>x</c> is the value:
    /// the constant <c>c</c>.
    /// </summary>
    private static Optional<object?>? ComparedConstant(IBinaryOperation equality, TrackedValue value, CaseSpace space)
    {
        var (left, right) = (Unconverted(equality.LeftOperand), Unconverted(equality.RightOperand));

        if (Tests(left, value, space) && right.ConstantValue.HasValue)
        {
            return right.ConstantValue;
        }

        if (Tests(right, value, space) && left.ConstantValue.HasValue)
        {
            return left.ConstantValue;
        }

        return null;

        static IOperation Unconverted(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conversion && !conversion.ConstantValue.HasValue)
            {
                operation = conversion.Operand;
            }

            return operation;
        }
    }

    /// <summary>
    /// The type of a constant, as written (before it's converted to what it's compared with).
    /// </summary>
    private static ITypeSymbol? ConstantType(IOperation constant)
    {
        while (constant is IConversionOperation { IsImplicit: true } conversion)
        {
            constant = conversion.Operand;
        }

        return constant.Type;
    }

    /// <summary>
    /// For <c>All</c> or <c>Any</c> on a collection that the value is an element of (see Narrowing):
    /// what its predicate says about the value.
    /// </summary>
    private static Outcome? ElementPredicate(IInvocationOperation call, TrackedValue value, CaseSpace space, Compilation compilation)
    {
        if (call.Arguments[0].Value is not { } source
            || TrackedValue.From(Sequences.Unconverted(source), value.Session) is not { } collection
            || !collection.IsPrefixOf(value)
            || value.Steps.Length <= collection.Steps.Length
            || value.Steps[collection.Steps.Length] is not ElementStep
            || call.Arguments[1].Value is not { } predicate
            || predicate.SemanticModel is not { } model
        )
        {
            return null;
        }

        return OfPredicate(predicate, [.. value.Steps.Skip(collection.Steps.Length + 1)], space, model, value.Session);
    }

    private static BinaryOperatorKind Flipped(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        _ => kind,
    };

    /// <summary>
    /// The body of a method, local function or property getter:
    /// the expression it returns, or its block.
    /// </summary>
    private static SyntaxNode? HelperBody(SyntaxNode declaration)
    {
        var (arrow, block) = declaration switch
        {
            MethodDeclarationSyntax method => (method.ExpressionBody, method.Body),
            LocalFunctionStatementSyntax local => (local.ExpressionBody, local.Body),
            PropertyDeclarationSyntax { ExpressionBody: { } expression } => (expression, null),
            PropertyDeclarationSyntax { AccessorList: { } accessors } when accessors.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } getter =>
                (getter.ExpressionBody, getter.Body),
            AccessorDeclarationSyntax accessor => (accessor.ExpressionBody, accessor.Body),
            _ => (null, null),
        };

        return (SyntaxNode?)arrow?.Expression ?? block;
    }

    /// <summary>
    /// The ways out of a helper's block that return normally, each with what's ruled out for the value there and what it returns (null for none), read statement by statement:
    /// through statements that don't return, and guards whose branches end in a <c>return</c> or a <c>throw</c> (<c>if (x is T) return ...;</c>, <c>if (x is T) throw ...;</c>), which rule out for what follows what their condition says.
    /// Null for a block that does anything else.
    /// (Nothing in it may write the value, which the caller makes sure of.)
    /// </summary>
    private static List<(CaseSet Excluded, ExpressionSyntax? Returned)>? Exits(BlockSyntax block, TrackedValue value, CaseSpace space, SemanticModel model, int depth)
    {
        var exits = new List<(CaseSet, ExpressionSyntax?)>();
        var excluded = CaseSet.Empty;

        // A branch's statements, the last of which leaves: what it returns, if it returns.
        bool Leaves(StatementSyntax branch, CaseSet excludedThere)
        {
            var statements = branch is BlockSyntax inner ? [.. inner.Statements] : new List<StatementSyntax> { branch };
            if (statements.Count == 0 || !statements.Take(statements.Count - 1).All(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax))
            {
                return false;
            }

            switch (statements[statements.Count - 1])
            {
                case ReturnStatementSyntax leaving:
                    exits.Add((excludedThere, leaving.Expression));
                    return true;
                case ThrowStatementSyntax:
                    return true;
                default:
                    return false;
            }
        }

        foreach (var statement in block.Statements)
        {
            switch (statement)
            {
                case ExpressionStatementSyntax or LocalDeclarationStatementSyntax:
                    continue;

                case ReturnStatementSyntax or ThrowStatementSyntax:
                    Leaves(statement, excluded);
                    return exits;

                case IfStatementSyntax guard when model.GetOperation(guard.Condition) is { } condition:
                    var outcome = Of(condition, value, space, model.Compilation, flag: null, depth);
                    if (!Leaves(guard.Statement, excluded | outcome.AlwaysFalse))
                    {
                        return null;
                    }

                    if (guard.Else is { } otherwise)
                    {
                        return Leaves(otherwise.Statement, excluded | outcome.AlwaysTrue) ? exits : null;
                    }

                    excluded |= outcome.AlwaysTrue;
                    continue;

                default:
                    return null;
            }
        }

        exits.Add((excluded, null));
        return exits;
    }

    /// <summary>
    /// Whether a pattern is a test for null (<c>null</c>), or for anything but (<c>not null</c>, <c>{ }</c>), or null if it's neither.
    /// </summary>
    private static bool? IsNullTest(IPatternOperation pattern) => pattern switch
    {
        IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } => true,
        INegatedPatternOperation { Pattern: IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } } => false,
        IRecursivePatternOperation { DeconstructionSubpatterns.IsEmpty: true, PropertySubpatterns.IsEmpty: true, Syntax: RecursivePatternSyntax { Type: null, PositionalPatternClause: null } } => false,
        _ => null,
    };

    /// <summary>
    /// For <c>x as T == null</c> or <c>null != x as T</c>, where x is the value:
    /// T.
    /// </summary>
    private static ITypeSymbol? NullComparedTryCast(IBinaryOperation equality, TrackedValue value, CaseSpace space, Func<ILocalSymbol, IOperation?>? flag) =>
        equality.RightOperand.ConstantValue is { HasValue: true, Value: null }
            ? TryCastOf(equality.LeftOperand, value, space, flag)
            : equality.LeftOperand.ConstantValue is { HasValue: true, Value: null }
                ? TryCastOf(equality.RightOperand, value, space, flag)
                : null;

    private static Outcome Of(IOperation condition, TrackedValue value, CaseSpace space, Compilation compilation, Func<ILocalSymbol, IOperation?>? flag, int depth)
    {
        Outcome OfOperand(IOperation operand) =>
            Of(operand, value, space, compilation, flag, depth);

        switch (condition)
        {
            // `if (flag)`, where the value is the bool itself.
            case var _ when space.Kind == SpaceKind.Bool && value.IsReadBy(condition):
                return new Outcome(CaseSet.Of([true]), CaseSet.Of([false]));

            // `true` and `false`, whatever the value is.
            case { ConstantValue: { HasValue: true, Value: bool constant } }:
                return constant ? new Outcome(space.All, CaseSet.Empty) : new Outcome(CaseSet.Empty, space.All);

            // `x == Color.Red`, `x != null`
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } equality
                when ComparedConstant(equality, value, space) is { } constant:
                var compared = OfConstant(constant.Value, space, byEquality: true);
                return equality.OperatorKind == BinaryOperatorKind.Equals ? compared : compared.Not();

            // `shape.Kind == ShapeKind.Circle`, where each case's Kind is a constant.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } discriminantEquality
                when (discriminantEquality.RightOperand.ConstantValue.HasValue ? (discriminantEquality.LeftOperand, discriminantEquality.RightOperand.ConstantValue) : (discriminantEquality.RightOperand, discriminantEquality.LeftOperand.ConstantValue)) is ({ } read, { HasValue: true } constant)
                    && OfDiscriminant(read, candidate => Equals(CaseSpace.ConstantKey(candidate), CaseSpace.ConstantKey(constant.Value)), value, space, compilation) is { } discriminant:
                return discriminantEquality.OperatorKind == BinaryOperatorKind.Equals ? discriminant : discriminant.Not();

            // `x as Circle != null`, which is `x is Circle` (also through a local set to `x as Circle`). A record's own
            // `==` compares with null the same way, if its Equals is the generated one.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } nullCheck
                when (nullCheck.OperatorMethod is null || IsGeneratedRecordEquality(nullCheck.OperatorMethod)) && NullComparedTryCast(nullCheck, value, space, flag) is { } castTo:
                var isCastTo = OfType(castTo, space, compilation);
                return nullCheck.OperatorKind == BinaryOperatorKind.NotEquals ? isCastTo : isCastTo.Not();

            // `(x is Bird) == false`, `isBird != true`: the bool compared, or its opposite.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null, IsLifted: false } boolEquality
                when BoolComparedWithConstant(boolEquality) is var (other, constant):
                var ofOther = OfOperand(other);
                return constant == (boolEquality.OperatorKind == BinaryOperatorKind.Equals) ? ofOther : ofOther.Not();

            // `(x is Bird) is true`, `isBird is not false`: the bool tested, or its opposite.
            case IIsPatternOperation { Value: { Type.SpecialType: SpecialType.System_Boolean } tested, Pattern: var boolPattern }
                when PatternHolds(boolPattern, true) is bool matchesTrue && PatternHolds(boolPattern, false) is bool matchesFalse && matchesTrue != matchesFalse:
                var ofTested = OfOperand(tested);
                return matchesTrue ? ofTested : ofTested.Not();

            // `maybe.HasValue`, which is `maybe != null`.
            case IPropertyReferenceOperation { Property: { Name: "HasValue", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }, Instance: { } nullable }
                when Tests(nullable, value, space):
                return OfConstant(null, space, byEquality: true).Not();

            // `u.TryGetValue(out Cat? c)` on a custom union with the non-boxing access pattern: the compiler matches
            // `u is Cat` by calling it, so it's true exactly when `u is Cat` is.
            case IInvocationOperation { TargetMethod: { Name: "TryGetValue", IsStatic: false, ReturnType.SpecialType: SpecialType.System_Boolean, Parameters.Length: 1 } tryGet, Instance: { } union }
                when space.IsUnion && tryGet.ContainingType.IsUnion && tryGet.Parameters[0].RefKind == RefKind.Out && Tests(union, value, space):
                var gotType = tryGet.Parameters[0].Type;
                return OfType(Symbols.UnderlyingOfNullable(gotType) ?? gotType.WithNullableAnnotation(NullableAnnotation.NotAnnotated), space, compilation);

            // `x > Size.Small`, on an enum.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual, OperatorMethod: null, IsLifted: false } comparison when space.Kind == SpaceKind.Enum:
                if (Tests(comparison.LeftOperand, value, space) && comparison.RightOperand.ConstantValue is { HasValue: true } right)
                {
                    return OfComparison(comparison.OperatorKind, right.Value, space);
                }

                if (Tests(comparison.RightOperand, value, space) && comparison.LeftOperand.ConstantValue is { HasValue: true } left)
                {
                    return OfComparison(Flipped(comparison.OperatorKind), left.Value, space);
                }

                return Outcome.Unknown;

            // `var isBird = x is Bird; if (isBird) ...`
            case ILocalReferenceOperation { Local: { Type.SpecialType: SpecialType.System_Boolean } local } when flag?.Invoke(local) is { } setTo:
                return OfOperand(setTo);

            // `x is A` (may bind as an is-type operator rather than a pattern), or `owner?.Pet is A` for owner.Pet
            case IIsTypeOperation isType when (Tests(isType.ValueOperand, value, space) || value.Equals(TrackedValue.FromNullConditional(isType.ValueOperand, value.Session)))
                && !(space.IsUnion && isType.ValueOperand.Type is ITypeParameterSymbol):
                var outcome = OfType(isType.TypeOperand, space, compilation);
                return isType.IsNegated ? outcome.Not() : outcome;

            // `x is A a`, `x is not (A or B)`, `x is null`, ...
            case IIsPatternOperation isPattern when Tests(isPattern.Value, value, space):
                return Of(isPattern.Pattern, space, compilation);

            // `x as Circle is null`, `(x as Circle) is { } c`: whether x is a Circle.
            case IIsPatternOperation isPattern when TryCastOf(isPattern.Value, value, space, flag) is { } castTo && IsNullTest(isPattern.Pattern) is bool isNull:
                var isCast = OfType(castTo, space, compilation);
                return isNull ? isCast.Not() : isCast;

            // `owner is { Pet: Bird }`, `owner is (Bird, _)`: what the pattern says about a member of what it tests.
            case IIsPatternOperation isPattern when OfPatternOn(isPattern.Value, isPattern.Pattern, value, space, compilation) is { } onMember:
                return onMember;

            // `(x, y) is (A, _)`: what the tuple pattern says about the element that's the value.
            case IIsPatternOperation { Value: ITupleOperation tuple } isPattern
                when tuple.Elements.Select((element, i) => (element, i)).FirstOrDefault(e => value.IsReadBy(e.element)) is (not null, var index):
                return OfTuplePattern(isPattern.Pattern, index, tuple.Elements.Length, space, compilation);

            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } not:
                return OfOperand(not.Operand).Not();

            // `x switch { Bird => true, _ => false }`, or the same over what x is a member of.
            case ISwitchExpressionOperation booleanSwitch when OfPatternOn(booleanSwitch.Value, booleanSwitch.Arms[0].Pattern, value, space, compilation) is not null:
                return OfBooleanSwitch(booleanSwitch, space, pattern => OfPatternOn(booleanSwitch.Value, pattern, value, space, compilation)!);

            // `a || b`, and `a | b` on bools, which evaluates both but is true in the same cases.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or, OperatorMethod: null } either when IsLogical(either):
                return Outcome.Or(OfOperand(either.LeftOperand), OfOperand(either.RightOperand));

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And, OperatorMethod: null } both when IsLogical(both):
                return Outcome.And(OfOperand(both.LeftOperand), OfOperand(both.RightOperand));

            // `c ? a : b` as a condition: a where c holds, b where it doesn't, and whatever both say either way. A branch
            // that throws is both true and false, since nothing after it runs.
            case IConditionalOperation { WhenFalse: { } whenFalse, Type.SpecialType: SpecialType.System_Boolean } conditional:
                Outcome Branch(IOperation branch) =>
                    IsThrow(branch) ? new Outcome(space.All, space.All) : OfOperand(branch);

                var test = OfOperand(conditional.Condition);
                var (ifTrue, ifFalse) = (Branch(conditional.WhenTrue), Branch(whenFalse));

                return new Outcome(
                    (test.AlwaysTrue & ifTrue.AlwaysTrue) | (test.AlwaysFalse & ifFalse.AlwaysTrue) | (ifTrue.AlwaysTrue & ifFalse.AlwaysTrue),
                    (test.AlwaysTrue & ifTrue.AlwaysFalse) | (test.AlwaysFalse & ifFalse.AlwaysFalse) | (ifTrue.AlwaysFalse & ifFalse.AlwaysFalse)
                );

            // `pets.All(p => p is not Bird)`, `pets.Any(p => p is Bird)`, where the value is an element of pets: All is
            // false if it's a case the predicate is false for, and Any is true if it's one the predicate is true for.
            case IInvocationOperation { TargetMethod: { Name: "All" or "Any" } quantifier } call when Sequences.IsEnumerableMethod(quantifier) && call.Arguments.Length == 2 && ElementPredicate(call, value, space, compilation) is { } predicate:
                return quantifier.Name == "All" ? new Outcome(CaseSet.Empty, predicate.AlwaysFalse) : new Outcome(predicate.AlwaysTrue, CaseSet.Empty);

            // `pet.IsBird()`, `Flies(pet)`, `pet.IsBird` (in the union, or an extension): what the helper's body says.
            case IInvocationOperation or IPropertyReferenceOperation when depth < Limits.HelperDepth && OfHelper(condition, value, space, compilation, depth) is { } helper:
                return helper;

            // `u.HasValue` on a custom union whose HasValue can't be read: the compiler matches `u is null` by calling
            // it, so it's true exactly when `u is not null` is.
            case IPropertyReferenceOperation { Property: { Name: "HasValue", IsStatic: false, Type.SpecialType: SpecialType.System_Boolean, ContainingType.IsUnion: true }, Instance: { } union } when space.IsUnion && Tests(union, value, space):
                return OfConstant(null, space).Not();

            default:
                return Outcome.Unknown;
        }
    }

    /// <summary>
    /// A switch expression giving a bool:
    /// true for the cases every possible arm gives true for, false for those every one gives false for.
    /// A case it throws for is both, since nothing after it runs.
    /// </summary>
    private static Outcome OfBooleanSwitch(ISwitchExpressionOperation switchOp, CaseSpace space, Func<IPatternOperation, Outcome> facts)
    {
        var byCase = ResultsByCase(switchOp, space, facts).ToList();

        CaseSet Cases(bool result)
        {
            var cases = byCase.Where(c => c.Results.All(r => r is null || IsThrow(r) || r.ConstantValue is { HasValue: true, Value: bool b } && b == result)).Select(c => c.Case).ToList();
            return CaseSet.Of(cases.OfType<object>(), includesNull: cases.Contains(null));
        }

        return new Outcome(Cases(true), Cases(false));
    }

    /// <summary>
    /// <c>x op constant</c>:
    /// on an enum (or a union's enum case type), true or false for each member's value;
    /// for any space, false for null.
    /// </summary>
    private static Outcome OfComparison(BinaryOperatorKind kind, object? constant, CaseSpace space, ITypeSymbol? constantType = null, Compilation? compilation = null)
    {
        if (CaseSpace.ConstantKey(constant) is not decimal bound)
        {
            return new Outcome(CaseSet.Empty, CaseSet.Null);
        }

        bool? Holds(object candidate) =>
            candidate is decimal value ? Compares(kind, value, bound) : null;

        if (space.IsUnion)
        {
            return OfConstantInUnion(bound, constantType, space, compilation, (_, values) => (values.Where(v => Holds(v) == true), values.Where(v => Holds(v) == false)));
        }

        if (space.Kind != SpaceKind.Enum)
        {
            return new Outcome(CaseSet.Empty, CaseSet.Null);
        }

        return new Outcome(
            CaseSet.Of(space.Cases.Where(candidate => Holds(candidate) == true)),
            CaseSet.Of(space.Cases.Where(candidate => Holds(candidate) == false), includesNull: true)
        );
    }

    /// <summary>
    /// What testing for a constant says:
    /// null matches only null.
    /// On a bool or enum, a value matches only its own case (an enum value no member has is one of the other values, but not all of them).
    /// On a union, the same goes for the values of a bool or enum case type, and a constant never matches a case that can't hold its type.
    /// On anything else, a constant matches only some values of a case, and never null.
    /// </summary>
    /// <param name="byEquality">Whether the test is <c>==</c> rather than a pattern.
    /// <c>x == null</c> on a nullable union is only true without a value, where <c>x is null</c> also matches a union that holds null.</param>
    private static Outcome OfConstant(object? constant, CaseSpace space, bool byEquality = false, ITypeSymbol? constantType = null, Compilation? compilation = null)
    {
        // Among a bool's or enum's values: the one that's the constant, and the ones that can't be.
        static (IEnumerable<object> trueValue, IEnumerable<object> falseValue) Named(object key, IReadOnlyCollection<object> values)
        {
            var named = values.Contains(key);
            return (named ? [key] : [], values.Where(c => !Equals(c, key) && (named || c != CaseSpace.OtherValues)));
        }

        if (constant is null)
        {
            return new Outcome(!byEquality || space.NullOnlyAsNoValue ? CaseSet.Null : CaseSet.Empty, CaseSet.Of(space.Cases));
        }

        if (space.IsUnion)
        {
            return OfConstantInUnion(CaseSpace.ConstantKey(constant)!, constantType, space, compilation, Named);
        }

        if (space.Kind is not (SpaceKind.Bool or SpaceKind.Enum))
        {
            return new Outcome(CaseSet.Empty, CaseSet.Null);
        }

        var (trueValue, falseValue) = Named(CaseSpace.ConstantKey(constant)!, space.Cases);

        return new Outcome(CaseSet.Of(trueValue), CaseSet.Of(falseValue, includesNull: true));
    }

    /// <summary>
    /// A constant (or comparison with one) on a union:
    /// worked out among the values of its case type that's the constant's type, if that's a bool or enum it lists the values of, with <paramref name="byValue"/>;
    /// false for every case that can't hold a value of that type, and for null.
    /// </summary>
    private static Outcome OfConstantInUnion(object key, ITypeSymbol? constantType, CaseSpace space, Compilation? compilation, Func<object, IReadOnlyCollection<object>, (IEnumerable<object> True, IEnumerable<object> False)> byValue)
    {
        if (constantType is null)
        {
            return new Outcome(CaseSet.Empty, CaseSet.Null);
        }

        var values = space.Cases.OfType<CaseValue>().Where(v => SymbolEqualityComparer.Default.Equals(v.Type, constantType)).ToList();
        var (alwaysTrue, alwaysFalse) = values.Count > 0 ? byValue(key, [.. values.Select(v => v.Key)]) : ([], []);
        var never = compilation is not CSharpCompilation csharp
            ? []
            : space.Cases.Where(c => CaseSpace.TypeOf(c) is { } type && !SymbolEqualityComparer.Default.Equals(type, constantType) && !CanBeInstanceOf(type, constantType, csharp));
        
        return new Outcome(
            CaseSet.Of(alwaysTrue.Select(k => (object)new CaseValue(constantType, k))),
            CaseSet.Of(alwaysFalse.Select(k => (object)new CaseValue(constantType, k)).Concat(never), includesNull: true)
        );
    }

    /// <summary>
    /// What a helper's body says about the value where it returns true, read as though written in place of the call, with the value as <paramref name="inBody"/>:
    /// the expression it returns, or for a block, what every way out returns, given what's ruled out there (see <see cref="Exits"/>).
    /// </summary>
    private static Outcome? OfBody(SyntaxNode body, TrackedValue inBody, CaseSpace space, SemanticModel model, int depth)
    {
        if (body is ExpressionSyntax expression)
        {
            return model.GetOperation(expression) is { Type.SpecialType: SpecialType.System_Boolean } operation ? Of(operation, inBody, space, model.Compilation, flag: null, depth) : null;
        }

        if (Exits((BlockSyntax)body, inBody, space, model, depth) is not { Count: > 0 } exits)
        {
            return null;
        }

        var (alwaysTrue, alwaysFalse) = (space.All, space.All);
        foreach (var (excluded, returned) in exits)
        {
            if (returned is null || model.GetOperation(returned) is not { Type.SpecialType: SpecialType.System_Boolean } operation)
            {
                return null;
            }

            var outcome = Of(operation, inBody, space, model.Compilation, flag: null, depth);

            alwaysTrue &= excluded | outcome.AlwaysTrue;
            alwaysFalse &= excluded | outcome.AlwaysFalse;
        }

        return new Outcome(alwaysTrue, alwaysFalse);
    }

    /// <summary>
    /// For a call to a helper that tests its argument (<c>static bool IsBird(Pet p) =&gt; p is Bird;</c>, an extension, or a union's own <c>bool IsBird =&gt; this is Bird;</c>):
    /// what its body says about the value, read as though it were written in place of the call, with the value passed for the parameter (or <c>this</c>).
    /// </summary>
    private static Outcome? OfHelper(IOperation call, TrackedValue value, CaseSpace space, Compilation compilation, int depth) =>
        InPlace(call, value, space, compilation) is var (body, inBody, bodySpace, model) ? OfBody(body, inBody, bodySpace, model, depth + 1) : null;

    /// <summary>
    /// For a call to a helper the value is passed to (as an argument, an extension's receiver, or <c>this</c>), or a member of it:
    /// the helper's body (see <see cref="HelperBody"/>), the value as it's reached there, the space to read the body in (with the call's type arguments), and the body's semantic model.
    /// </summary>
    /// <remarks>Only for a helper declared in this compilation, that can't be overridden or dispatched elsewhere, isn't async, and doesn't write the parameter it gets the value as.</remarks>
    private static (SyntaxNode Body, TrackedValue InBody, CaseSpace Space, SemanticModel Model)? InPlace(IOperation call, TrackedValue value, CaseSpace space, Compilation compilation)
    {
        // What the receiver stands for in the body: `this` (as its type), or an extension's parameter.
        static ISymbol? Receiver(ISymbol member) => member switch
        {
            { IsStatic: true } when member is IMethodSymbol { IsExtensionMethod: true } => null, // `x.M()` binds x to the first parameter
            { ContainingType: { IsExtension: true, ExtensionParameter: { } extension } } => extension,
            { IsStatic: true } => null,
            _ => member.ContainingType,
        };

        var (member, bindings) = call switch
        {
            IInvocationOperation invocation => (
                (ISymbol)invocation.TargetMethod,
                invocation.Arguments.Select(a => (a.Value, (ISymbol?)a.Parameter)).Prepend((invocation.Instance!, Receiver(invocation.TargetMethod))).ToList()
            ),

            IPropertyReferenceOperation { Arguments.IsEmpty: true } property => (
                property.Property,
                new List<(IOperation, ISymbol?)> { (property.Instance!, Receiver(property.Property)) }
            ),

            _ => default,
        };

        if (member is null or IMethodSymbol { IsAsync: true })
        {
            return null;
        }

        var definition = member.OriginalDefinition;
        if (definition is IMethodSymbol { ReducedFrom: { } reduced })
        {
            definition = reduced;
        }

        if (Functions.OwnDeclaration(definition, compilation) is not { } declaration || HelperBody(declaration) is not { } body)
        {
            return null;
        }

        // The argument the value is (or is a member of), and what stands for it in the body.
        foreach (var (argument, parameter) in bindings)
        {
            if (argument is null || parameter is null || TrackedValue.From(argument, value.Session) is not { } passed || !passed.IsPrefixOf(value) && !passed.Equals(value))
            {
                continue;
            }

            var inBody = parameter is ITypeSymbol self ? TrackedValue.ThisOf(self.OriginalDefinition, value.Session) : TrackedValue.OfVariable(parameter.OriginalDefinition, value.Session);
            var model = compilation.GetSemanticModel(body.SyntaxTree);

            return parameter is IParameterSymbol && WriteAnalysis.Writes(body, inBody, model)
                ? null
                : (body, value.Rebased(passed, inBody), space.Reading(member, compilation), model);
        }

        return null;
    }

    /// <summary>
    /// The cases of the value for which a call to a helper it's passed to never returns:
    /// those ruled out at every way out of the helper's block (<c>static void Settled(Data d) { if (d is Loading) throw ...; }</c>, an assertion).
    /// </summary>
    public static CaseSet ThrowsWhenCalled(IInvocationOperation call, TrackedValue value, CaseSpace space, Compilation compilation)
    {
        if (InPlace(call, value, space, compilation) is not (BlockSyntax block, var inBody, var bodySpace, var model) || Exits(block, inBody, bodySpace, model, depth: 1) is not { } exits)
        {
            return CaseSet.Empty;
        }

        return exits.Aggregate(space.All, (throwing, exit) => throwing & exit.Excluded);
    }

    /// <summary>
    /// What <paramref name="pattern"/> says about the member reached through <paramref name="path"/> from what it tests, assuming that isn't null.
    /// </summary>
    /// <remarks>A <see cref="CaseStep"/> in the path is a step into what a union holds, as that case:
    /// it's there only when the union holds that case.</remarks>
    private static Outcome OfMemberPattern(IPatternOperation pattern, IReadOnlyList<Step> path, CaseSpace space, Compilation compilation)
    {
        if (path.Count == 0)
        {
            return Of(pattern, space, compilation);
        }

        var csharp = (CSharpCompilation)compilation;
        ITypeSymbol Read(ITypeSymbol type) =>
            space.Interpret(type, compilation);

        var step = path[0] is CaseStep { Type: var stepType } ? Read(stepType) : null;

        // The pattern's input is already what the step goes into (`Bird` in `Pet and Bird`).
        if (step is not null && SymbolEqualityComparer.Default.Equals(Read(Symbols.UnderlyingOfNullable(pattern.InputType) ?? pattern.InputType), step))
        {
            return OfMemberPattern(pattern, [.. path.Skip(1)], space, compilation);
        }

        var everything = new Outcome(space.All, CaseSet.Empty);
        var nothing = new Outcome(CaseSet.Empty, space.All);

        switch (pattern)
        {
            case IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }:
                return everything;

            // A type test on a non-null value that's always that type (`Owner o`) doesn't constrain the member. Where
            // the path steps into a case, the member is there only when the union holds that case, so a test for it
            // is true, and one for a case it can't be is false.
            case ITypePatternOperation or IDeclarationPatternOperation when step is not null:
                return AlwaysInstanceOf(step, Read(pattern.NarrowedType), csharp) ? everything
                    : !CanBeInstanceOf(step, Read(pattern.NarrowedType), csharp) ? nothing
                    : Outcome.Unknown;
            case ITypePatternOperation or IDeclarationPatternOperation:
                return AlwaysInstanceOf(Read(pattern.InputType), Read(pattern.NarrowedType), csharp) ? everything : Outcome.Unknown;

            case IRecursivePatternOperation stepping when step is not null:
                if (!CanBeInstanceOf(step, Read(stepping.MatchedType), csharp))
                {
                    return nothing;
                }

                if (!AlwaysInstanceOf(step, Read(stepping.MatchedType), csharp))
                {
                    return Outcome.Unknown;
                }

                // Its subpatterns are about members of the case.
                var rest = path.Skip(1).ToList();

                return rest.Count == 0
                    ? (SubpatternsAlwaysMatch(stepping) ? everything : Outcome.Unknown)
                    : OfSubpatterns(stepping, rest, typeHolds: true);

            case IRecursivePatternOperation recursive:
                return OfSubpatterns(recursive, path, typeHolds: AlwaysInstanceOf(Read(recursive.InputType), Read(recursive.MatchedType), csharp));

            case INegatedPatternOperation not:
                return OfMemberPattern(not.Pattern, path, space, compilation).Not();

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or:
                return Outcome.Or(OfMemberPattern(or.LeftPattern, path, space, compilation), OfMemberPattern(or.RightPattern, path, space, compilation));

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                return Outcome.And(OfMemberPattern(and.LeftPattern, path, space, compilation), OfMemberPattern(and.RightPattern, path, space, compilation));

            default:
                return Outcome.Unknown;
        }

        // What a recursive pattern's subpattern for (the start of) the path says; the rest of it has to match anything
        // (and its type test hold) for the pattern to be sure to match.
        Outcome OfSubpatterns(IRecursivePatternOperation recursive, IReadOnlyList<Step> steps, bool typeHolds)
        {
            var parts = new List<(IReadOnlyList<Step> Steps, IPatternOperation Pattern)>();
            for (var i = 0; i < recursive.DeconstructionSubpatterns.Length; i++)
            {
                parts.Add((PositionalMember(recursive, i) is { } member ? [new MemberStep(member)] : [], recursive.DeconstructionSubpatterns[i]));
            }

            foreach (var property in recursive.PropertySubpatterns)
            {
                parts.Add((MemberChain(property.Member), property.Pattern));
            }

            var on = parts.FirstOrDefault(part => part.Steps.Count > 0 && part.Steps.Count <= steps.Count && part.Steps.SequenceEqual(steps.Take(part.Steps.Count)));
            if (on.Pattern is null)
            {
                return Outcome.Unknown;
            }

            var outcome = OfMemberPattern(on.Pattern, [.. steps.Skip(on.Steps.Count)], space, compilation);
            var restMatches = typeHolds && parts.Where(part => part.Pattern != on.Pattern).All(part => MatchesAnything(part.Pattern));
            return restMatches ? outcome : new Outcome(CaseSet.Empty, outcome.AlwaysFalse);
        }
    }

    /// <summary>
    /// What a pattern on a tuple says about its element at <paramref name="index"/>:
    /// false wherever the element's subpattern is, and true wherever it is, if the other subpatterns match anything.
    /// </summary>
    private static Outcome OfTuplePattern(IPatternOperation pattern, int index, int length, CaseSpace space, Compilation compilation) =>
        OfTuplePattern(pattern, index, length, space, element => Of(element, space, compilation));

    /// <summary>
    /// A type test, which on a union tests the union's <c>Value</c>:
    /// true for every case type whose every non-null value is a <paramref name="type"/>, false for null and for every case type none of whose values can be one.
    /// </summary>
    private static Outcome OfType(ITypeSymbol type, CaseSpace space, Compilation compilation)
    {
        type = space.Interpret(type, compilation);

        // What a test against a union type means (the union itself, or its Value?) isn't something to guess at, unless
        // the union is one of this union's case types: then it's a test of the Value, like any other case.
        if (type.IsUnion && !(space.UnionType is { } union && union.UnionCaseTypes.Contains(type, SymbolEqualityComparer.Default)))
        {
            return Outcome.Unknown;
        }

        var csharp = (CSharpCompilation)compilation;

        // A bool, enum or anything else: every non-null value is the value's own type, so it's true for all of them if
        // that's always a `type`, and false if it never can be.
        if (!space.IsOfTypes)
        {
            return space.Type is null
                ? new Outcome(CaseSet.Empty, CaseSet.Null)
                : AlwaysInstanceOf(space.Type, type, csharp)
                    ? new Outcome(CaseSet.Of(space.Cases), CaseSet.Null)
                    : !CanBeInstanceOf(space.Type, type, csharp)
                        ? new Outcome(CaseSet.Empty, space.All)
                        : new Outcome(CaseSet.Empty, CaseSet.Null);
        }

        return new Outcome(
            CaseSet.Of(space.Cases.Where(c => CaseSpace.TypeOf(c) is { } caseType && AlwaysInstanceOf(caseType, type, csharp))),
            CaseSet.Of(space.Cases.Where(c => CaseSpace.TypeOf(c) is { } caseType && !CanBeInstanceOf(caseType, type, csharp)), includesNull: true)
        );
    }

    /// <summary>
    /// For each case, what a switch expression over the value can give for it:
    /// the results of the arms that might match it, up to the first that's sure to, and a null for falling off the end (which throws).
    /// </summary>
    private static IEnumerable<(object? Case, IReadOnlyList<IOperation?> Results)> ResultsByCase(ISwitchExpressionOperation switchOp, CaseSpace space, Func<IPatternOperation, Outcome> facts)
    {
        var outcomes = switchOp.Arms.Select(arm => (arm, outcome: facts(arm.Pattern))).ToList();

        foreach (var candidate in space.Cases.Cast<object?>().Append(null))
        {
            var results = new List<IOperation?>();
            var sure = false;

            foreach (var (arm, outcome) in outcomes)
            {
                if (outcome.AlwaysFalse.ContainsCase(candidate))
                {
                    continue;
                }

                results.Add(arm.Value);

                if (arm.Guard is null && outcome.AlwaysTrue.ContainsCase(candidate))
                {
                    sure = true;
                    break;
                }
            }

            if (!sure)
            {
                results.Add(null);
            }

            yield return (candidate, results);
        }
    }

    /// <summary>
    /// Whether every subpattern of a recursive pattern matches whatever it's given, like <c>Cat { Name: _ }</c> or <c>Dog(var name)</c>.
    /// </summary>
    /// <remarks>Only for a property named directly (<c>{ A.B: _ }</c> also requires <c>A</c> not to be null), and for positional patterns that call a Deconstruct method or take a tuple apart (on an <c>ITuple</c>, even <c>()</c> checks its Length).</remarks>
    private static bool SubpatternsAlwaysMatch(IRecursivePatternOperation recursive)
    {
        if (recursive.Syntax is RecursivePatternSyntax { PositionalPatternClause: not null } && !(recursive.DeconstructSymbol is IMethodSymbol || recursive.MatchedType.IsTupleType))
        {
            return false;
        }

        return recursive.DeconstructionSubpatterns.All(MatchesAnything)
            && recursive.PropertySubpatterns.All(property =>
                property.Syntax is SubpatternSyntax { NameColon: not null } or SubpatternSyntax { ExpressionColon.Expression: IdentifierNameSyntax }
                && MatchesAnything(property.Pattern)
            );
    }

    /// <summary>
    /// Whether a test of <paramref name="operand"/> is a test of the value:
    /// it reads the value, assigns it (then gives what it assigned), or reads a union value's <c>Value</c>, which type patterns test anyway (and patterns on an object mean the same for it).
    /// Conditions that write the value are only read where that's been allowed for (see Narrowing).
    /// </summary>
    private static bool Tests(IOperation operand, TrackedValue value, CaseSpace space) =>
        value.IsReadBy(operand)
        || (operand is ISimpleAssignmentOperation { Target: var target } && value.IsReadBy(target)) // `(x = e) is A`, where x then holds what's tested
        || (space.IsUnion
            && operand is IPropertyReferenceOperation { Property: { Name: "Value", Type.SpecialType: SpecialType.System_Object }, Instance: { Type.IsUnion: true } instance }
            && value.IsReadBy(instance)
        );

    /// <summary>
    /// For <c>x as T</c>, where x is the value (or a local set to that, which still means it):
    /// T, which the result is null exactly when x isn't.
    /// (On a union, x is its Value.)
    /// Not for a cast to a union or a type parameter.
    /// </summary>
    private static ITypeSymbol? TryCastOf(IOperation operand, TrackedValue value, CaseSpace space, Func<ILocalSymbol, IOperation?>? flag)
    {
        while (operand is IConversionOperation { IsImplicit: true } implicitConversion)
        {
            operand = implicitConversion.Operand;
        }

        if (operand is ILocalReferenceOperation { Local: var local } && flag?.Invoke(local) is { } setTo)
        {
            operand = setTo;
        }

        return operand is IConversionOperation { IsTryCast: true, Operand: var cast, Type: { } type } && Tests(cast, value, space)
            && (Symbols.UnderlyingOfNullable(type) ?? type) is { TypeKind: not TypeKind.TypeParameter, IsUnion: false } castTo
            ? castTo.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : null;
    }

    /// <summary>
    /// Whether every non-null value of the case (a type of a union or closed hierarchy, or any value of a bool, enum or other type) is a <paramref name="type"/>.
    /// </summary>
    public static bool CaseIsAlways(object candidate, ITypeSymbol type, CaseSpace space, Compilation compilation) =>
        CaseSpace.TypeOf(candidate) is { } caseType
            ? AlwaysInstanceOf(caseType, type, (CSharpCompilation)compilation)
            : space.Type is { } valueType && AlwaysInstanceOf(valueType, type, (CSharpCompilation)compilation);

    /// <summary>
    /// The member a deconstruction reads at <paramref name="index"/> from a value of <paramref name="type"/>:
    /// a tuple's element (as <c>Item1</c>, ...), or what <paramref name="deconstruct"/> gives there, where that's known.
    /// </summary>
    public static ISymbol? DeconstructedMember(ITypeSymbol? type, IMethodSymbol? deconstruct, int index) => type switch
    {
        INamedTypeSymbol { IsTupleType: true } tuple when index < tuple.TupleElements.Length =>
            tuple.TupleElements[index].CorrespondingTupleField ?? tuple.TupleElements[index],
        _ when deconstruct is not null => DeconstructedMember(deconstruct, index),
        _ => null,
    };

    /// <summary>
    /// The member a Deconstruct method gives for its out parameter at <paramref name="index"/>, where that's known:
    /// the property a record's generated Deconstruct reads, <c>Key</c> and <c>Value</c> for a <c>KeyValuePair</c>, and <c>Item1</c>, <c>Item2</c>, ...
    /// for a <c>System.Tuple</c>.
    /// </summary>
    public static ISymbol? DeconstructedMember(IMethodSymbol deconstruct, int index)
    {
        if (deconstruct is { IsImplicitlyDeclared: true, ContainingType.IsRecord: true } && index < deconstruct.Parameters.Length)
        {
            return deconstruct.ContainingType.GetMembers(deconstruct.Parameters[index].Name).OfType<IPropertySymbol>().FirstOrDefault();
        }

        if (Symbols.IsKeyValuePair(deconstruct.ContainingType) && index < 2)
        {
            return deconstruct.ContainingType.GetMembers(index == 0 ? "Key" : "Value").OfType<IPropertySymbol>().FirstOrDefault();
        }

        // `TupleExtensions.Deconstruct(this Tuple<T1, ...> value, out T1 item1, ...)`
        if (deconstruct.IsExtensionMethod && Symbols.Is(deconstruct.ContainingType, "TupleExtensions", 0, "System")
            && (deconstruct.ReducedFrom ?? deconstruct).Parameters is { Length: > 1 } parameters
            && parameters[0].Type is INamedTypeSymbol tuple && Symbols.IsSystemTuple(tuple) && index < 7
        )
        {
            return tuple.GetMembers("Item" + (index + 1)).OfType<IPropertySymbol>().FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Whether a binary operator is a logical one on bools (not a lifted one on <c>bool?</c>, nor a bitwise one on integers).
    /// </summary>
    public static bool IsLogical(IBinaryOperation operation) =>
        !operation.IsLifted
        && operation.LeftOperand.Type?.SpecialType == SpecialType.System_Boolean
        && operation.RightOperand.Type?.SpecialType == SpecialType.System_Boolean;

    /// <summary>
    /// Whether evaluating the operation always throws:
    /// it's a <c>throw</c> expression, or a call to a method that never returns (<c>[DoesNotReturn]</c>, or a throw helper like <c>static T Fail&lt;T&gt;() =&gt; throw ...;</c>).
    /// </summary>
    public static bool IsThrow(IOperation operation) => operation switch
    {
        IThrowOperation => true,
        IConversionOperation { Operand: var operand } => IsThrow(operand),
        IInvocationOperation { TargetMethod: var method } => Functions.NeverReturns(method),
        _ => false,
    };

    /// <summary>
    /// Whether a pattern matches whatever it's given:
    /// <c>_</c>, <c>var x</c>, and combinations of them.
    /// </summary>
    public static bool MatchesAnything(IPatternOperation pattern) => pattern switch
    {
        IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => true,
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => MatchesAnything(or.LeftPattern) || MatchesAnything(or.RightPattern),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => MatchesAnything(and.LeftPattern) && MatchesAnything(and.RightPattern),
        _ => false,
    };

    /// <summary>
    /// The members a property subpattern reads:
    /// <c>{ Owner.Pet: ... }</c> reads Owner, then Pet.
    /// </summary>
    public static IReadOnlyList<Step> MemberChain(IOperation member)
    {
        var chain = new List<Step>();

        for (var current = member; current is IMemberReferenceOperation reference; current = reference.Instance)
        {
            chain.Insert(0, new MemberStep(reference.Member));

            if (reference.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.PatternInput })
            {
                return chain;
            }
        }

        return [];
    }

    /// <summary>
    /// What a value of static type <paramref name="type"/> (or of a type derived from it) can't be:
    /// the cases none of whose values can be a <paramref name="type"/>.
    /// (Not null: the value might be.)
    /// </summary>
    public static CaseSet NotOfType(ITypeSymbol type, CaseSpace space, Compilation compilation) =>
        CaseSet.Of(OfType(type, space, compilation).AlwaysFalse.Cases);

    /// <summary>
    /// What a pattern matched against a value in <paramref name="space"/> says about its case.
    /// </summary>
    public static Outcome Of(IPatternOperation pattern, CaseSpace space, Compilation compilation)
    {
        // A pattern on another union, the one a case holds (`Bird` in `Pet and Bird` on a union of Pets and Rocks),
        // says nothing about this one's cases. Nor does one on a type parameter: generic code tests a union it's given
        // boxed, which is never one of its cases.
        if (space.IsUnion && (pattern.InputType is ITypeParameterSymbol || AppliesToAnotherUnion(pattern.InputType, space, compilation)))
        {
            return Outcome.Unknown;
        }

        switch (pattern)
        {
            // `_` and `var x` match anything (on a union, `var x` binds the union itself).
            case IDiscardPatternOperation:
            case IDeclarationPatternOperation { MatchesNull: true }:
                return new Outcome(space.All, CaseSet.Empty);

            case ITypePatternOperation typePattern:
                return OfType(typePattern.MatchedType, space, compilation);

            case IDeclarationPatternOperation { MatchedType: { } matchedType }:
                return OfType(matchedType, space, compilation);

            // `{ Value: P }` on a union is P (patterns on a union test its Value anyway), and on a `Pet?` (or a class
            // union), P on the union, which only matches if there is one.
            case IRecursivePatternOperation { DeconstructionSubpatterns.IsEmpty: true, PropertySubpatterns.Length: 1 } recursive
                when space.IsUnion
                    && recursive.Syntax is RecursivePatternSyntax { Type: null }
                    && recursive.PropertySubpatterns[0] is { Member: IPropertyReferenceOperation { Property.Name: "Value" } member } valuePattern
                    && (member.Property.ContainingType.IsUnion || member.Property.ContainingType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T):
                var ofValue = Of(valuePattern.Pattern, space, compilation);
                return recursive.InputType is { IsUnion: true, IsValueType: true }
                    ? ofValue
                    : new Outcome(CaseSet.Of(ofValue.AlwaysTrue.Cases), ofValue.AlwaysFalse); // `{ }` also checks there's a union

            // `T { ... }` and `T(...)`: false wherever the type test is false; true wherever it's true, if every
            // subpattern matches anything. A subpattern on a discriminant (`{ Kind: ShapeKind.Circle }`) says what it
            // says about the cases.
            case IRecursivePatternOperation recursive:
                var typeTest = OfType(recursive.MatchedType, space, compilation);
                if (SubpatternsAlwaysMatch(recursive))
                {
                    return typeTest;
                }

                var onDiscriminants = recursive.PropertySubpatterns
                    .Select(subpattern => subpattern is { Member: IPropertyReferenceOperation { Property: var property, Instance: IInstanceReferenceOperation } }
                        ? OfDiscriminant(property, constant => PatternHolds(subpattern.Pattern, constant), space, compilation)
                        : null
                    )
                    .ToList();
                var together = onDiscriminants.Aggregate(typeTest, (outcome, discriminant) => Outcome.And(outcome, discriminant ?? Outcome.Unknown));
                
                return recursive.Syntax is RecursivePatternSyntax { PositionalPatternClause: null } && onDiscriminants.All(discriminant => discriminant is not null)
                    ? together
                    : new Outcome(CaseSet.Empty, together.AlwaysFalse);

            case IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: var constant } } constantPattern:
                return OfConstant(constant, space, constantType: ConstantType(constantPattern.Value), compilation: compilation);

            case IRelationalPatternOperation relational:
                return OfComparison(relational.OperatorKind, relational.Value.ConstantValue is { HasValue: true } c ? c.Value : null, space, ConstantType(relational.Value), compilation);

            case INegatedPatternOperation not:
                return Of(not.Pattern, space, compilation).Not();

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or:
                return Outcome.Or(Of(or.LeftPattern, space, compilation), Of(or.RightPattern, space, compilation));

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                return Outcome.And(Of(and.LeftPattern, space, compilation), Of(and.RightPattern, space, compilation));

            default:
                return Outcome.Unknown;
        }
    }

    /// <summary>
    /// What a constant case label on a switch over a discriminant of the value (<c>switch (shape.Kind)</c>) says about it, or null if that's not what the switch is over.
    /// </summary>
    public static Outcome? OfConstantOnDiscriminant(IOperation input, ISingleValueCaseClauseOperation clause, TrackedValue value, CaseSpace space, Compilation compilation) =>
        clause.Value.ConstantValue is { HasValue: true } constant
            ? OfDiscriminant(input, candidate => Equals(CaseSpace.ConstantKey(candidate), CaseSpace.ConstantKey(constant.Value)), value, space, compilation)
            : null;

    /// <summary>
    /// Whether the operation reads a discriminant of the value (see <see cref="OfDiscriminant(IPropertySymbol, Func{object?, bool?}, CaseSpace, Compilation)"/>), whose tests say something about it.
    /// </summary>
    public static bool ReadsDiscriminant(IOperation operation, TrackedValue value, CaseSpace space, Compilation compilation) =>
        OfDiscriminant(operation, _ => null, value, space, compilation) is not null;

    /// <summary>
    /// What a switch statement's case label says about the case of the value switched on.
    /// </summary>
    public static Outcome Of(ICaseClauseOperation clause, CaseSpace space, Compilation compilation) => clause switch
    {
        IPatternCaseClauseOperation patternClause => Of(patternClause.Pattern, space, compilation),
        ISingleValueCaseClauseOperation { Value.ConstantValue: { HasValue: true, Value: var constant } } single => OfConstant(constant, space, constantType: ConstantType(single.Value), compilation: compilation),
        _ => Outcome.Unknown,
    };

    /// <summary>
    /// What a condition says about the case of <paramref name="value"/>.
    /// </summary>
    /// <param name="flag">For a local in the condition, the value it was set to, where testing the local still means testing that (see Narrowing), or null.</param>
    public static Outcome Of(IOperation condition, TrackedValue value, CaseSpace space, Compilation compilation, Func<ILocalSymbol, IOperation?>? flag = null) =>
        Of(condition, value, space, compilation, flag, depth: 0);

    /// <summary>
    /// What a <c>when</c> clause says about the value:
    /// read as it is, and also through a variable the arm's pattern binds to the whole input, where the input is the value or what it's a member of (<c>p</c> in <c>var p when p is Bird</c>, or <c>o</c> in <c>Owner o when o.Pet is Bird</c> on a closed hierarchy).
    /// Each reading only knows what it sees, so what either says holds.
    /// </summary>
    public static Outcome OfGuard(IOperation input, IPatternOperation pattern, IOperation guard, TrackedValue value, CaseSpace space, SemanticModel model, Func<ILocalSymbol, IOperation?>? flag = null)
    {
        // `var p`, or a typed designation that binds the same object (not a case taken out of a union), through `and`s
        // that don't narrow what they pass on.
        static IEnumerable<ILocalSymbol> WholeInputDesignations(IPatternOperation pattern) => pattern switch
        {
            IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol local, MatchesNull: true } => [local],
            IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol typed } when BindsSameObject(pattern, typed) => [typed],
            IRecursivePatternOperation { DeclaredSymbol: ILocalSymbol typed } when BindsSameObject(pattern, typed) => [typed],
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } both =>
                WholeInputDesignations(both.LeftPattern).Concat(SymbolEqualityComparer.Default.Equals(both.RightPattern.InputType, pattern.InputType) ? WholeInputDesignations(both.RightPattern) : []),
            _ => [],
        };

        // A reference to the same object as the input (not what a union holds).
        static bool BindsSameObject(IPatternOperation pattern, ILocalSymbol local) =>
            pattern.InputType is { IsUnion: false, IsReferenceType: true } && local.Type.IsReferenceType && !local.Type.IsUnion;

        var outcome = Of(guard, value, space, model.Compilation, flag);
        var owner = value.IsReadBy(input)
            ? value
            : TrackedValue.From(input, value.Session) is { } read && read.IsPrefixOf(value)
                ? read
                : null;

        if (owner is null)
        {
            return outcome;
        }

        foreach (var bound in WholeInputDesignations(pattern))
        {
            var alias = value.Rebased(owner, TrackedValue.OfVariable(bound, value.Session));

            if (WriteAnalysis.Writes(guard.Syntax, alias, model))
            {
                continue;
            }

            var viaAlias = Of(guard, alias, space, model.Compilation);

            outcome = new Outcome(outcome.AlwaysTrue | viaAlias.AlwaysTrue, outcome.AlwaysFalse | viaAlias.AlwaysFalse);
        }

        return outcome;
    }

    /// <summary>
    /// What a method whose body is a single test (<c>static bool IsNotBird(Pet p) =&gt; p is not Bird;</c>) says about its parameter at <paramref name="ordinal"/> (or what <paramref name="steps"/> read from it), when it returns true;
    /// or null if it isn't a method like that.
    /// </summary>
    public static Outcome? OfMethodBody(IMethodSymbol method, int ordinal, IReadOnlyList<Step> steps, CaseSpace space, Compilation compilation, Session session)
    {
        space = space.Reading(method, compilation);
        method = method.ReducedFrom ?? method.OriginalDefinition;

        if (ordinal >= method.Parameters.Length
            || Functions.OwnDeclaration(method, compilation) is not { } declaration
            || HelperBody(declaration) is not { } body
        )
        {
            return null;
        }

        var model = compilation.GetSemanticModel(body.SyntaxTree);
        var parameter = TrackedValue.Path(method.Parameters[ordinal], steps, session);

        return WriteAnalysis.Writes(body, parameter, model) ? null : OfBody(body, parameter, space, model, depth: 1);
    }

    /// <summary>
    /// What a pattern on <paramref name="input"/> says about the value:
    /// the pattern's own facts if the input is the value, and if the value is a member of the input (<c>owner.Pet</c> for a pattern on <c>owner</c>), what its property or positional subpatterns for that member say.
    /// Null if the input is neither.
    /// </summary>
    /// <remarks>
    /// Wherever the member is read, what it's read from isn't null (reading it would throw), so a pattern's null checks along the way don't matter:
    /// <c>owner is { Pet: Bird }</c> is false for a null owner, but then owner.Pet isn't read either.
    /// A subpattern is followed through a named property or field (<c>{ Pet: ... }</c>, <c>{ Owner.Pet: ... }</c>), a tuple element, or a record's generated Deconstruct (<c>(Bird, _)</c>).
    /// </remarks>
    public static Outcome? OfPatternOn(IOperation input, IPatternOperation pattern, TrackedValue value, CaseSpace space, Compilation compilation)
    {
        if (Tests(input, value, space))
        {
            return Of(pattern, space, compilation);
        }

        if (OfDiscriminant(input, constant => PatternHolds(pattern, constant), value, space, compilation) is { } discriminant)
        {
            return discriminant;
        }

        // A `?.` chain says the same about what it reads, wherever that's read (see FromNullConditional).
        if ((TrackedValue.From(input, value.Session) ?? TrackedValue.FromNullConditional(input, value.Session)) is not { } owner || !owner.IsPrefixOf(value))
        {
            return null;
        }

        return OfMemberPattern(pattern, [.. value.Steps.Skip(owner.Steps.Length)], space, compilation);
    }

    /// <summary>
    /// What a predicate (a lambda that returns a condition, or a method whose body is one) says about the element it's given (or what <paramref name="steps"/> read from it).
    /// </summary>
    public static Outcome? OfPredicate(IOperation predicate, IReadOnlyList<Step> steps, CaseSpace space, SemanticModel model, Session session)
    {
        switch (Sequences.Unconverted(predicate))
        {
            case IAnonymousFunctionOperation { Symbol.Parameters.Length: > 0, Body.Operations.Length: 1 } filter when filter.Body.Operations[0] is IReturnOperation { ReturnedValue: { } condition }:
                var element = TrackedValue.Path(filter.Symbol.Parameters[0], steps, session);
                return WriteAnalysis.Writes(condition.Syntax, element, model) ? null : Of(condition, element, space, model.Compilation);

            case IMethodReferenceOperation { Method: var method }:
                return OfMethodBody(method, 0, steps, space, model.Compilation, session);

            default:
                return null;
        }
    }

    /// <summary>
    /// What a pattern on a tuple says about its element at <paramref name="index"/>, given what a subpattern for that element says (<paramref name="element"/>):
    /// false wherever the element's subpattern is, and true wherever it is, if the other subpatterns match anything.
    /// </summary>
    public static Outcome OfTuplePattern(IPatternOperation pattern, int index, int length, CaseSpace space, Func<IPatternOperation, Outcome> element)
    {
        switch (pattern)
        {
            case IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }:
                return new Outcome(space.All, CaseSet.Empty);

            case IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == length:
                var at = element(recursive.DeconstructionSubpatterns[index]);
                var othersMatchAnything = recursive.DeconstructionSubpatterns.Where((_, i) => i != index).All(MatchesAnything);
                return new Outcome(othersMatchAnything ? at.AlwaysTrue : CaseSet.Empty, at.AlwaysFalse);

            case INegatedPatternOperation not:
                return OfTuplePattern(not.Pattern, index, length, space, element).Not();

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or:
                return Outcome.Or(OfTuplePattern(or.LeftPattern, index, length, space, element), OfTuplePattern(or.RightPattern, index, length, space, element));

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                return Outcome.And(OfTuplePattern(and.LeftPattern, index, length, space, element), OfTuplePattern(and.RightPattern, index, length, space, element));

            default:
                return Outcome.Unknown;
        }
    }

    /// <summary>
    /// The member a positional subpattern reads:
    /// a tuple's element, or a record's property, through the Deconstruct the compiler generated for it.
    /// </summary>
    public static ISymbol? PositionalMember(IRecursivePatternOperation recursive, int index) =>
        DeconstructedMember(recursive.MatchedType, recursive.DeconstructSymbol as IMethodSymbol, index);

    /// <summary>
    /// The cases for which evaluating the switch expression always throws:
    /// every arm that might match them throws, or none does.
    /// </summary>
    /// <param name="facts">What each arm's pattern says about the value.</param>
    public static CaseSet ThrowingCases(ISwitchExpressionOperation switchOp, CaseSpace space, Func<IPatternOperation, Outcome> facts)
    {
        var throwing = ResultsByCase(switchOp, space, facts).Where(c => c.Results.All(r => r is null || IsThrow(r))).Select(c => c.Case).ToList();
        return CaseSet.Of(throwing.OfType<object>(), includesNull: throwing.Contains(null));
    }
}

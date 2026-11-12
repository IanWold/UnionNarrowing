using System.Collections.Immutable;

namespace UnionNarrowing.Suppressors.Analysis;

file class Marker(string name)
{
    public override string ToString() =>
        name;
}

/// <summary>
/// The cases a value can be in:
/// the case types of a union or the leaf types of a closed hierarchy (or, for an element of a tuple switch, the values of a bool or enum), plus whether null is a value the compiler wants handled.
/// </summary>
internal class CaseSpace
{
    private enum Reach
    {
        Types,
        Constants,
        Anything,
    }

    /// <summary>
    /// For a union, the case type each case that came from expanding one (see <see cref="Expand"/>) is part of.
    /// </summary>
    private readonly ImmutableDictionary<object, ITypeSymbol> _origins;

    /// <summary>
    /// The single case of an <see cref="SpaceKind.Opaque"/> space:
    /// any non-null value.
    /// </summary>
    public static readonly object Anything = new Marker("anything");

    /// <summary>
    /// For an enum, every value that isn't one of its members'.
    /// </summary>
    public static readonly object OtherValues = new Marker("other values");

    /// <summary>
    /// Every case, null included.
    /// </summary>
    public CaseSet All =>
        CaseSet.Of(Cases, includesNull: true);

    /// <summary>
    /// Every non-null case:
    /// for a union, its case types (except that one that's closed is its leaf types, and one that's a bool or enum its values, as <see cref="CaseValue"/>s);
    /// for a closed hierarchy, its leaf types;
    /// for a bool or enum, its values.
    /// </summary>
    public ImmutableArray<object> Cases { get; }

    /// <summary>
    /// Whether this is a union or a closed hierarchy, whose cases are types.
    /// </summary>
    public bool IsOfTypes =>
        Kind is SpaceKind.Union or SpaceKind.Closed;

    /// <summary>
    /// Whether this is a union, whose patterns test its <c>Value</c>, rather than a closed hierarchy.
    /// </summary>
    public bool IsUnion =>
        Kind == SpaceKind.Union;

    public SpaceKind Kind { get; }

    /// <summary>
    /// Whether the compiler would want null handled in a switch over this value (CS8655).
    /// </summary>
    public bool NullMatters { get; }

    /// <summary>
    /// Whether null can only mean the variable itself holds nothing (a null reference, or a nullable value without a value), never a case that holds null.
    /// Then the compiler's nullable flow state says whether it can be null.
    /// </summary>
    public bool NullOnlyAsNoValue { get; }

    /// <summary>
    /// For a bool, enum or opaque space, the value's type (for a nullable value type, the type inside).
    /// </summary>
    public ITypeSymbol? Type { get; }

    /// <summary>
    /// What the type parameters of the generic code being read stand for (see <see cref="Reading"/>):
    /// a type written there is read through these before it's compared with the cases.
    /// </summary>
    public TypeArguments TypeArguments { get; }

    /// <summary>
    /// For a union, the union type (for a nullable union, the one inside).
    /// </summary>
    public ITypeSymbol? UnionType { get; }

    private CaseSpace(SpaceKind kind, ImmutableArray<object> cases, bool nullMatters, ITypeSymbol? type = null, bool nullOnlyAsNoValue = false, ITypeSymbol? unionType = null, ImmutableDictionary<object, ITypeSymbol>? origins = null)
    {
        UnionType = unionType;
        Kind = kind;
        Cases = cases;
        NullMatters = nullMatters;
        Type = type;
        NullOnlyAsNoValue = nullOnlyAsNoValue;
        _origins = origins ?? ImmutableDictionary<object, ITypeSymbol>.Empty;
        TypeArguments = TypeArguments.None;
    }

    private CaseSpace(CaseSpace space, TypeArguments typeArguments)
        : this(space.Kind, space.Cases, space.NullMatters, space.Type, space.NullOnlyAsNoValue, space.UnionType, space._origins) =>
        TypeArguments = typeArguments;

    private static CaseSpace? Create(ITypeSymbol? type, NullableFlowState flowState, bool warnings, Reach reach, SemanticModel model, CancellationToken ct)
    {
        if (type is null)
        {
            return null;
        }

        var maybeNull = flowState == NullableFlowState.MaybeNull;

        if (UnionSpace(type, maybeNull, warnings, model, ct) is { } union)
        {
            return union;
        }

        // For a reference, the compiler goes by its flow state: a `Shape?` that's been checked for null needs no
        // null arm.
        if (type is { IsClosed: true } closedType)
        {
            return new CaseSpace(SpaceKind.Closed, Leaves(closedType, ct).CastArray<object>(), warnings && maybeNull, nullOnlyAsNoValue: true);
        }

        if (reach == Reach.Types)
        {
            return null;
        }

        // For a nullable value type, null matters unless the flow state says it has a value; for a reference, where
        // nullable warnings are on and it may be null.
        var (underlying, nullMatters) = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue
            ? (nullableValue.TypeArguments[0], !(warnings && flowState == NullableFlowState.NotNull))
            : (type, !type.IsValueType && warnings && maybeNull);

        if (underlying.SpecialType == SpecialType.System_Boolean)
        {
            return new CaseSpace(SpaceKind.Bool, [true, false], nullMatters, underlying, nullOnlyAsNoValue: true);
        }

        if (underlying is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            return new CaseSpace(SpaceKind.Enum, [.. EnumValues(enumType, model, ct)], nullMatters, underlying, nullOnlyAsNoValue: true);
        }

        if (reach == Reach.Constants || underlying.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer || underlying.IsUnion || underlying.IsTupleType)
        {
            return null;
        }

        return new CaseSpace(SpaceKind.Opaque, [Anything], nullMatters, underlying, nullOnlyAsNoValue: true);
    }

    /// <summary>
    /// An enum's cases:
    /// its members' distinct values, and, unless CS8524 (which is about the rest) is turned off, <see cref="OtherValues"/>.
    /// </summary>
    private static List<object> EnumValues(INamedTypeSymbol enumType, SemanticModel model, CancellationToken ct)
    {
        var values = enumType.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue)
            .Select(field => ConstantKey(field.ConstantValue))
            .OfType<object>()
            .Distinct()
            .ToList();

        if (!IsTurnedOff("CS8524", model, ct))
        {
            values.Add(OtherValues);
        }

        return values;
    }

    /// <summary>
    /// A union's cases:
    /// its case types, except that one that's closed is its leaf types, and one that's a <c>bool</c> or an enum is its values (as <see cref="CaseValue"/>s), the way the compiler counts them too:
    /// <c>NotFound</c> and <c>Timeout</c> handle a closed <c>Error</c>, and <c>true</c> and <c>false</c> a <c>bool</c>.
    /// Also, for each case that came from expanding a case type, which one.
    /// </summary>
    private static (ImmutableArray<object> Cases, ImmutableDictionary<object, ITypeSymbol> Origins) Expand(ImmutableArray<ITypeSymbol> caseTypes, SemanticModel model, CancellationToken ct)
    {
        var cases = new List<object>();
        var origins = ImmutableDictionary.CreateBuilder<object, ITypeSymbol>(CaseComparer.Instance);
        
        foreach (var caseType in caseTypes)
        {
            var underlying = Symbols.UnderlyingOfNullable(caseType) ?? caseType;
            IEnumerable<object>? parts = underlying switch
            {
                { IsClosed: true } => Leaves(underlying, ct),
                { SpecialType: SpecialType.System_Boolean } => [new CaseValue(underlying, true), new CaseValue(underlying, false)],
                INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType => EnumValues(enumType, model, ct).Select(value => (object)new CaseValue(enumType, value)),
                _ => null,
            };

            if (parts is null)
            {
                if (!cases.Contains(caseType, CaseComparer.Instance))
                {
                    cases.Add(caseType);
                }

                continue;
            }

            foreach (var part in parts)
            {
                if (cases.Contains(part, CaseComparer.Instance))
                {
                    continue;
                }

                cases.Add(part);
                origins[part] = caseType;
            }
        }

        return (cases.ToImmutableArray(), origins.ToImmutable());
    }

    private static NullableFlowState FlowState(IOperation value, SemanticModel model, CancellationToken ct) =>
        model.GetTypeInfo(value.Syntax, ct).Nullability.FlowState;

    /// <summary>
    /// Whether an instance of the class can't change once constructed:
    /// it's sealed, derives straight from object, and all its instance fields are readonly, with no settable properties or field-like events (whose storage isn't).
    /// For a class from another assembly, whose private fields a reference doesn't show, the fields are read from its metadata.
    /// </summary>
    private static bool IsImmutable(INamedTypeSymbol type)
    {
        if (type is not { TypeKind: TypeKind.Class, IsSealed: true, BaseType.SpecialType: SpecialType.System_Object })
        {
            return false;
        }

        if (type.DeclaringSyntaxReferences.IsEmpty)
        {
            return MetadataFields.AllInstanceFieldsReadonly(type);
        }

        return type.GetMembers().All(member => member switch
        {
            IFieldSymbol { IsStatic: false } field => field.IsReadOnly || field.IsConst,
            IPropertySymbol { IsStatic: false } property => property.SetMethod is null or { IsInitOnly: true },
            IEventSymbol { IsStatic: false } => false,
            _ => true,
        });
    }

    /// <summary>
    /// Whether what a union holds can only change with the union itself:
    /// declared with <c>union</c>, or written by hand (<c>[Union]</c> on a class or struct) with its Value, and the HasValue and TryGetValue patterns may call instead, reading only its own fields and auto-properties, never what one of them refers to (an array element, a box's contents), which another reference could change.
    /// One from another assembly is taken at its word.
    /// </summary>
    private static bool ReadsOnlyItsOwnState(ITypeSymbol union, Compilation compilation, CancellationToken ct)
    {
        static bool ReadsOtherState(IOperation operation) => operation switch
        {
            IFieldReferenceOperation { Field.IsConst: true } or IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation } => false,
            IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: var property } => property.IsIndexer || !Symbols.IsAutoProperty(property),
            IMemberReferenceOperation or IArrayElementReferenceOperation or IInvocationOperation => true,
            IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation => true,
            _ => false,
        };

        if (union.DeclaringSyntaxReferences.IsEmpty || union.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is UnionDeclarationSyntax))
        {
            return true;
        }

        var readers = union.GetMembers()
            .Where(member => member is { IsStatic: false } and (IPropertySymbol { Name: "Value" or "HasValue" } or IMethodSymbol { Name: "TryGetValue" }))
            .Where(member => member is not IPropertySymbol property || !Symbols.IsAutoProperty(property))
            .Select(member => member as IMethodSymbol ?? ((IPropertySymbol)member).GetMethod)
            .OfType<IMethodSymbol>();

        return readers.SelectMany(reader => reader.DeclaringSyntaxReferences).All(reference =>
            reference.GetSyntax(ct) is var body
            && compilation.SyntaxTrees.Contains(body.SyntaxTree)
            && compilation.GetSemanticModel(body.SyntaxTree).GetOperation(body, ct) is { } operation
            && !operation.DescendantsAndSelf().Any(ReadsOtherState)
        );
    }

    /// <summary>
    /// Whether a warning is turned off for the whole tree, by NoWarn or .editorconfig (not #pragma).
    /// </summary>
    private static bool IsTurnedOff(string id, SemanticModel model, CancellationToken ct)
    {
        var options = model.Compilation.Options;
        if (options.SpecificDiagnosticOptions.TryGetValue(id, out var specific) && specific == ReportDiagnostic.Suppress)
        {
            return true;
        }

        if (options.SyntaxTreeOptionsProvider is { } provider)
        {
            if (provider.TryGetDiagnosticValue(model.SyntaxTree, id, ct, out var forTree))
            {
                return forTree == ReportDiagnostic.Suppress;
            }

            if (provider.TryGetGlobalDiagnosticValue(id, ct, out var global))
            {
                return global == ReportDiagnostic.Suppress;
            }
        }

        return false;
    }

    /// <summary>
    /// The types a value of a closed type can actually have:
    /// its closed derived types, with any that are closed themselves (and so abstract) replaced by theirs.
    /// </summary>
    private static ImmutableArray<ITypeSymbol> Leaves(ITypeSymbol closedType, CancellationToken ct) =>
        [.. closedType.GetClosedDerivedTypeInfo(ct).ClosedDerivedTypes.SelectMany(derived => derived.IsClosed ? Leaves(derived, ct) : [derived])];

    /// <summary>
    /// Whether a case type can hold null.
    /// Errs towards yes, which only means suppressing less.
    /// </summary>
    private static bool MayBeNull(ITypeSymbol type) => type switch
    {
        { IsValueType: true } => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
        ITypeParameterSymbol parameter =>
            parameter.NullableAnnotation == NullableAnnotation.Annotated
            || !(parameter.HasNotNullConstraint
                || parameter.HasUnmanagedTypeConstraint
                || (parameter.HasReferenceTypeConstraint && parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.NotAnnotated)
                || parameter.ConstraintTypes.Any(c => c.NullableAnnotation == NullableAnnotation.NotAnnotated && !MayBeNull(c))
            ),
        _ => type.NullableAnnotation == NullableAnnotation.Annotated,
    };

    /// <summary>
    /// Whether some case type can hold null.
    /// </summary>
    /// <remarks><see cref="ITypeSymbol.UnionCaseTypes"/> drops nullable annotations (<c>union U(Cat?, Dog)</c> reports plain <c>Cat</c>), so the annotations come from the union's creation members, its single-parameter constructors, which is where the compiler gets them too.</remarks>
    private static bool UnionMayHoldNull(ITypeSymbol unionType, ImmutableArray<ITypeSymbol> caseTypes) =>
        caseTypes.Any(MayBeNull)
        || (unionType is INamedTypeSymbol named && named.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 1
            && caseTypes.Contains(constructor.Parameters[0].Type, SymbolEqualityComparer.Default)
            && MayBeNull(constructor.Parameters[0].Type)
        ));

    /// <summary>
    /// The case space of a union:
    /// a struct union, which only holds null as a case;
    /// or a <c>Pet?</c> (patterns go through to the union, and <c>is null</c> covers no value as well as a default union inside) or a class union that can't change once it's made (so neither can its Value), either of which can also be null itself, as the compiler goes by the flow state for.
    /// </summary>
    private static CaseSpace? UnionSpace(ITypeSymbol type, bool maybeNull, bool warnings, SemanticModel model, CancellationToken ct)
    {
        var (union, canBeNullItself) = type switch
        {
            { IsUnion: true, IsValueType: true } => (type, false),
            _ when Symbols.UnderlyingOfNullable(type) is { IsUnion: true } inner => (inner, true),
            INamedTypeSymbol { IsUnion: true, IsReferenceType: true } classUnion when IsImmutable(classUnion) => (classUnion, true),
            _ => (null, false),
        };

        if (union is not { UnionCaseTypes: { IsEmpty: false } caseTypes } || !ReadsOnlyItsOwnState(union, model.Compilation, ct))
        {
            return null;
        }

        var caseCanBeNull = UnionMayHoldNull(union, caseTypes);
        var (cases, origins) = Expand(caseTypes, model, ct);

        return canBeNullItself
            ? new CaseSpace(SpaceKind.Union, cases, warnings && (maybeNull || caseCanBeNull), nullOnlyAsNoValue: !caseCanBeNull, unionType: union, origins: origins)
            : new CaseSpace(SpaceKind.Union, cases, warnings && caseCanBeNull, unionType: union, origins: origins);
    }

    private static bool Warnings(int position, SemanticModel model) =>
        model.GetNullableContext(position).WarningsEnabled();

    /// <summary>
    /// The cases, with the parts of a union's case type replaced by the case type itself wherever every one of them is there:
    /// <c>Error</c> for <c>NotFound</c> and <c>Timeout</c>, <c>bool</c> for <c>true</c> and <c>false</c>.
    /// That's how to name them, in a message or a pattern.
    /// </summary>
    public IReadOnlyList<object> Collapsed(IEnumerable<object> cases)
    {
        var list = cases.ToList();
        var result = new List<object>();

        foreach (var candidate in list)
        {
            if (_origins.TryGetValue(candidate, out var origin) && _origins.Where(pair => SymbolEqualityComparer.Default.Equals(pair.Value, origin)).All(pair => list.Contains(pair.Key, CaseComparer.Instance)))
            {
                if (!result.Contains(origin, CaseComparer.Instance))
                {
                    result.Add(origin);
                }
            }
            else
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// A type written in the code being read, as it is there: with what its type parameters stand for (see <see cref="TypeArguments"/>).
    /// </summary>
    public ITypeSymbol Interpret(ITypeSymbol type, Compilation compilation) =>
        TypeArguments.Apply(type, compilation);

    /// <summary>
    /// This space of generic code, as it is at a use of <paramref name="member"/> (a call to the method whose parameter it's the space of):
    /// its case types (in the same order) and union with what the type parameters stand for there in place.
    /// </summary>
    public CaseSpace At(ISymbol member, Compilation compilation)
    {
        var typeArguments = TypeArguments.For(member, compilation);
        if (!IsOfTypes || typeArguments == TypeArguments)
        {
            return this;
        }

        object Constructed(object candidate) =>
            candidate is ITypeSymbol type ? typeArguments.Apply(type, compilation) : candidate;

        return new CaseSpace(
            Kind,
            [.. Cases.Select(Constructed)],
            NullMatters,
            Type is null ? null : typeArguments.Apply(Type, compilation),
            NullOnlyAsNoValue,
            UnionType is null ? null : typeArguments.Apply(UnionType, compilation),
            _origins.ToImmutableDictionary(pair => Constructed(pair.Key), pair => typeArguments.Apply(pair.Value, compilation), CaseComparer.Instance)
        );
    }

    /// <summary>
    /// What <paramref name="excluded"/>, in <paramref name="at"/> (this space at a use, see <see cref="At"/>), rules out of this space's cases: those in the same places.
    /// </summary>
    public CaseSet FromUse(CaseSet excluded, CaseSpace at) =>
        at == this ? excluded : CaseSet.Of(Cases.Where((_, i) => excluded.ContainsCase(at.Cases[i])), excluded.IncludesNull);

    /// <summary>
    /// This space, for reading the code of <paramref name="member"/> in place of a use of it (a helper's body for a call to it, a method's returns for what a call gives):
    /// its type parameters, and those of the types it's in, stand for the use's type arguments.
    /// </summary>
    public CaseSpace Reading(ISymbol member, Compilation compilation) =>
        TypeArguments.For(member, compilation) is var typeArguments && typeArguments != TypeArguments ? new CaseSpace(this, typeArguments) : this;

    /// <summary>
    /// A constant as a case:
    /// integral values (an enum's, whatever its underlying type) compare by value.
    /// </summary>
    public static object? ConstantKey(object? constant) => constant switch
    {
        null => null,
        bool => constant,
        sbyte or byte or short or ushort or int or uint or long or ulong or char => System.Convert.ToDecimal(constant, System.Globalization.CultureInfo.InvariantCulture),
        _ => constant,
    };

    /// <summary>
    /// Whether a default value of the type holds none of its cases:
    /// it's a struct union, and no case type can be null (otherwise the default is just that null).
    /// </summary>
    public static bool DefaultHoldsNoCase(ITypeSymbol? type) =>
        type is { IsUnion: true, IsValueType: true } && !UnionMayHoldNull(type, type.UnionCaseTypes);

    /// <summary>
    /// The case space of <paramref name="value"/>, if it's a union or of a closed type.
    /// (A class-based custom union counts only if it can't change its Value without the variable being reassigned. A closed hierarchy is fine as a class, because an object's type never changes.)
    /// </summary>
    public static CaseSpace? Of(IOperation value, SemanticModel model, CancellationToken ct) =>
        Create(value.Type, FlowState(value, model, ct), Warnings(value.Syntax.SpanStart, model), Reach.Types, model, ct);

    /// <summary>
    /// The case space of an element of a tuple switch:
    /// as for <see cref="OfInput"/>, or for any other type, a single case that only a pattern matching every value covers.
    /// </summary>
    public static CaseSpace? OfElement(IOperation value, SemanticModel model, CancellationToken ct) =>
        Create(value.Type, FlowState(value, model, ct), Warnings(value.Syntax.SpanStart, model), Reach.Anything, model, ct);

    /// <summary>
    /// The case space of an element of a tuple-typed variable, going by the element's declared type (its annotation stands in for a flow state, erring towards null mattering).
    /// </summary>
    public static CaseSpace? OfElement(ITypeSymbol type, int position, SemanticModel model, CancellationToken ct) =>
        Create(type, (type.IsValueType ? Symbols.UnderlyingOfNullable(type) is null : type.NullableAnnotation == NullableAnnotation.NotAnnotated) ? NullableFlowState.NotNull : NullableFlowState.MaybeNull, Warnings(position, model), Reach.Anything, model, ct);

    /// <summary>
    /// The case space of a switch's whole input:
    /// a union or closed type as for <see cref="Of"/>, or a bool or enum, whose values can be listed.
    /// </summary>
    public static CaseSpace? OfInput(IOperation value, SemanticModel model, CancellationToken ct) =>
        Create(value.Type, FlowState(value, model, ct), Warnings(value.Syntax.SpanStart, model), Reach.Constants, model, ct);

    /// <summary>
    /// The type a case's values have:
    /// the case itself for a type, or the bool or enum a <see cref="CaseValue"/> is a value of.
    /// </summary>
    public static ITypeSymbol? TypeOf(object candidate) => candidate switch
    {
        ITypeSymbol type => type,
        CaseValue value => value.Type,
        _ => null,
    };
}

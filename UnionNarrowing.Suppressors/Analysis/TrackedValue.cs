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
/// A value whose case the analysis follows from one statement to the next:
/// a by-value local or parameter, or a chain of members that can't change (readonly fields, get-only auto-properties, getters that just return one of those) starting from one of those, from <c>this</c> in a class, or from a static member.
/// Fields of a struct (a tuple's elements, say) count too, wherever the struct itself is followed:
/// they only change when it's written.
/// </summary>
internal class TrackedValue : IEquatable<TrackedValue>
{
    /// <summary>
    /// Where a value's path starts.
    /// </summary>
    private enum Start
    {
        /// <summary>
        /// A local or parameter.
        /// </summary>
        Variable,

        /// <summary>
        /// <c>this</c>: in a class, or, when following mutable state, in a struct.
        /// </summary>
        This,

        /// <summary>
        /// A static member, which is the path's first step.
        /// </summary>
        Static,

        /// <summary>
        /// <c>this</c> in the body of a struct's own member, read as though it were inline (see <see cref="ThisOf"/>):
        /// it's only ever compared with <c>this</c> there, never followed.
        /// </summary>
        StructThisInHelper,
    }

    private readonly Start _start;

    /// <summary>
    /// The variable, for <see cref="Start.Variable"/>, or the struct, for <see cref="Start.StructThisInHelper"/>.
    /// </summary>
    private readonly ISymbol? _startSymbol;

    /// <summary>
    /// The variables the value is indexed by, which writing changes what it is.
    /// </summary>
    public IEnumerable<ISymbol> IndexVariables =>
        Steps.OfType<IndexStep>().Select(step => step.Variable);

    /// <summary>
    /// Whether the value is one that something else could change without this code visibly writing it (another thread, or code this code calls), which the analysis only follows when asked to (see <see cref="Session.FollowMutableStateKey"/>):
    /// it's read through a field that isn't readonly, a settable auto-property, <c>this</c> in a struct, or an array element.
    /// </summary>
    public bool IsExternallyMutable { get; }

    /// <summary>
    /// The fields and properties the value is read through.
    /// </summary>
    public IEnumerable<ISymbol> MembersReadThrough =>
        Steps.OfType<MemberStep>().Select(step => step.Member);

    /// <summary>
    /// The run of the analysis the value is followed in, whose options say what can be followed.
    /// Values made from this one (its members, what reads it) are followed in it too.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    /// The steps from where the value starts to the value, outermost first.
    /// </summary>
    public ImmutableArray<Step> Steps { get; }

    /// <summary>
    /// The local or parameter the value is read from, or null if it starts from <c>this</c> or a static member.
    /// Those can't be written (outside constructors), so only a variable needs watching.
    /// </summary>
    public ISymbol? Variable =>
        _start == Start.Variable ? _startSymbol : null;

    private TrackedValue(Session session, Start start, ISymbol? startSymbol, ImmutableArray<Step> steps, bool isExternallyMutable)
    {
        Session = session;
        _start = start;
        _startSymbol = startSymbol;
        Steps = steps;
        IsExternallyMutable = isExternallyMutable;
    }

    /// <summary>
    /// Whether reading the property always gives the same value once the object is constructed:
    /// it's a non-virtual auto-property with no setter (an <c>init</c> accessor is fine), or one whose getter just returns its <c>field</c>, or another such member of the object (<c>public Pet Pet =&gt; _pet;</c> over a readonly field).
    /// </summary>
    /// <remarks>In source, the declaration shows whether it's an auto-property.
    /// From another assembly, see <see cref="HasReadonlyBackingField"/>.</remarks>
    /// <param name="session">The run the property's getter is followed in, if it has to be.
    /// Whether that follows mutable state doesn't matter:
    /// a getter that reads mutable state can change either way.</param>
    private static bool CantChange(IPropertySymbol property, Compilation? compilation, int depth, Session session)
    {
        // An anonymous type's properties (`new { p, q = p }`, and query `let`) are get-only.
        if (property.ContainingType.IsAnonymousType)
        {
            return true;
        }

        if (property.IsIndexer || property.IsVirtual || property.IsAbstract || property.IsOverride || property.GetMethod is not { } getter)
        {
            return false;
        }

        // A KeyValuePair's Key and Value, and a System.Tuple's items, which just return readonly fields.
        if ((Symbols.IsKeyValuePair(property.ContainingType) && property.Name is "Key" or "Value") || (Symbols.IsSystemTuple(property.ContainingType) && property.Name.StartsWith("Item", StringComparison.Ordinal) && property.Name.Length == 5))
        {
            return true;
        }

        // A struct's auto-property is a field of the struct, which only changes when the struct does, setter or not
        // (setting it writes the struct).
        if (property.ContainingType.IsValueType && property.SetMethod is not (null or { IsInitOnly: true }))
        {
            return Symbols.IsAutoProperty(property);
        }

        if (property.SetMethod is not (null or { IsInitOnly: true }))
        {
            return false;
        }

        if (property.DeclaringSyntaxReferences.IsEmpty)
        {
            return Symbols.IsCompilerGenerated(getter) && HasReadonlyBackingField(property);
        }

        return property.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() switch
        {
            PropertyDeclarationSyntax { ExpressionBody: { } body } => Returns(body.Expression),
            PropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.Accessors.All(accessor =>
                accessor.Body is null && accessor.ExpressionBody is null
                || !accessor.IsKind(SyntaxKind.GetAccessorDeclaration) // init only runs during construction
                || accessor.ExpressionBody is { Expression: var returned } && Returns(returned)
                || accessor.Body is { Statements: { Count: 1 } statements } && statements[0] is ReturnStatementSyntax { Expression: { } value } && Returns(value)
            ),
            ParameterSyntax => property.ContainingType.IsRecord, // a positional record property
            _ => false,
        });

        // The getter returns its `field`, or a chain from `this` (in a class) or a static member that can't change.
        bool Returns(ExpressionSyntax expression) =>
            IsField(expression)
            || (depth < Limits.GetterDepth
                && compilation is not null
                && compilation.SyntaxTrees.Contains(expression.SyntaxTree)
                && From(compilation.GetSemanticModel(expression.SyntaxTree).GetOperation(expression), depth + 1, session) is { Variable: null, IsExternallyMutable: false }
            );
    }

    /// <summary>
    /// The <c>?.</c> whose receiver the instance stands for.
    /// </summary>
    private static IConditionalAccessOperation? ConditionalAccessOf(IConditionalAccessInstanceOperation instance)
    {
        IOperation current = instance;
        for (var parent = instance.Parent; parent is not null; current = parent, parent = parent.Parent)
        {
            if (parent is IConditionalAccessOperation access && access.WhenNotNull == current)
            {
                return access;
            }
        }

        return null;
    }

    private static TrackedValue? From(IOperation? operation, int depth, Session session) => operation switch
    {
        ILocalReferenceOperation { Local: { IsRef: false } local } => Starting(session, Start.Variable, local),
        
        IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } parameter } => Starting(session, Start.Variable, parameter),

        // References to storage elsewhere, which only follow when asked to.
        ILocalReferenceOperation { Local: { IsRef: true } local } reference when session.FollowsMutableState(reference.Syntax.SyntaxTree) =>
            Starting(session, Start.Variable, local, isExternallyMutable: true),
        
        IParameterReferenceOperation { Parameter: { RefKind: RefKind.In or RefKind.Ref or RefKind.RefReadOnlyParameter } parameter } reference when session.FollowsMutableState(reference.Syntax.SyntaxTree) =>
            Starting(session, Start.Variable, parameter, isExternallyMutable: true),

        // `this` in a struct can be reassigned wholesale (`this = ...`), fields and all, and it's a reference to wherever
        // the struct lives. Only followed when asked to.
        IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type.IsReferenceType: true } => Starting(session, Start.This),
        
        IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type.IsValueType: true } self when session.FollowsMutableState(self.Syntax.SyntaxTree) =>
            Starting(session, Start.This, isExternallyMutable: true),

        // `pets[i]`: an ImmutableArray's element never changes; an array's only when asked to follow it.
        IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } indexer when Symbols.IsImmutableArray(indexer.Property.ContainingType)
                && From(indexer.Arguments[0].Value, depth, session) is { Steps.IsEmpty: true, Variable: { } index } =>
            From(indexer.Instance, depth, session)?.With(new IndexStep(index)),
        
        IArrayElementReferenceOperation { Indices.Length: 1 } element when session.FollowsMutableState(element.Syntax.SyntaxTree)
            && From(element.Indices[0], depth, session) is { Steps.IsEmpty: true, Variable: { } index } =>
            From(element.ArrayReference, depth, session)?.With(new IndexStep(index), isExternallyMutable: true),

        // `pets[0]`: by a constant, likewise.
        IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } constantIndexer when Symbols.IsImmutableArray(constantIndexer.Property.ContainingType)
                && CaseSpace.ConstantKey(constantIndexer.Arguments[0].Value.ConstantValue is { HasValue: true } k ? k.Value : null) is { } key =>
            From(constantIndexer.Instance, depth, session)?.With(new ConstantIndexStep(constantIndexer.Property.OriginalDefinition, key)),
        
        IArrayElementReferenceOperation { Indices.Length: 1 } constantElement when session.FollowsMutableState(constantElement.Syntax.SyntaxTree)
            && CaseSpace.ConstantKey(constantElement.Indices[0].ConstantValue is { HasValue: true } k ? k.Value : null) is { } key
            && constantElement.SemanticModel?.Compilation.GetSpecialType(SpecialType.System_Array).GetMembers("GetValue").OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Int32) is { } getValue =>
            From(constantElement.ArrayReference, depth, session)?.With(new ConstantIndexStep(getValue, key), isExternallyMutable: true),

        // The receiver of a `?.`, inside what it leads to: `owner` in `owner?.Pet`.
        IConditionalAccessInstanceOperation instance when ConditionalAccessOf(instance) is { } access => From(access.Operation, depth, session),

        // `(Pet)pet` is just pet.
        IConversionOperation { Conversion.IsIdentity: true, OperatorMethod: null } conversion => From(conversion.Operand, depth, session),

        // `maybe.Value` on a `Pet?` is the union inside, which patterns on `maybe` test anyway.
        IPropertyReferenceOperation { Property: { Name: "Value", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }, Instance: { } nullable } =>
            From(nullable, depth, session),

        IFieldReferenceOperation { Field: var field } reference => Member(reference.Instance, field, reference, depth, session),
        
        IPropertyReferenceOperation { Property: var property, Arguments.IsEmpty: true } reference => Member(reference.Instance, property, reference, depth, session),

        _ => null,
    };

    /// <summary>
    /// For a property from another assembly:
    /// whether its auto-property backing field is <c>initonly</c>, which the compiler does exactly when the property has no setter other than <c>init</c>.
    /// </summary>
    /// <remarks>
    /// A reference only imports what other assemblies can see, so neither the private backing field nor a private setter shows up as a symbol (<c>{ get; private set; }</c> looks just like <c>{ get; }</c>).
    /// The raw metadata has both.
    /// </remarks>
    private static bool HasReadonlyBackingField(IPropertySymbol property) =>
        MetadataFields.IsInitOnly(property.ContainingType, "<" + property.MetadataName + ">k__BackingField") == true;

    /// <summary>
    /// Whether the expression is the property's <c>field</c>.
    /// </summary>
    private static bool IsField(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.FieldExpression);

    /// <summary>
    /// Reading <paramref name="member"/>, a static one, or one of what <paramref name="instance"/> is, where that can be followed (see <see cref="ReadsMutableState"/>).
    /// </summary>
    private static TrackedValue? Member(IOperation? instance, ISymbol member, IOperation reference, int depth, Session session)
    {
        var followsMutableState = session.FollowsMutableState(reference.Syntax.SyntaxTree);
        if (ReadsMutableState(member, reference.SemanticModel?.Compilation, depth, followsMutableState, session) is not { } isExternallyMutable)
        {
            return null;
        }

        var owner = member.IsStatic ? Starting(session, Start.Static) : From(instance, depth, session);
        return owner?.With(new MemberStep(member), isExternallyMutable);
    }

    /// <summary>
    /// Whether reading the member can be followed, and if so, whether only something else could change what it gives (see <see cref="IsExternallyMutable"/>):
    /// null if it can't be followed.
    /// A readonly field, a field of a struct (which only changes when the struct does), and a property that can't change are followed;
    /// a field that isn't readonly or a settable auto-property only when asked to.
    /// </summary>
    private static bool? ReadsMutableState(ISymbol member, Compilation? compilation, int depth, bool followsMutableState, Session session) => member switch
    {
        IFieldSymbol { IsReadOnly: true } => false,
        IFieldSymbol { IsStatic: false, ContainingType.IsValueType: true } => false,
        IPropertySymbol property when CantChange(property, compilation, depth, session) => false,
        IFieldSymbol { IsConst: false } when followsMutableState => true,
        IPropertySymbol { IsIndexer: false, IsVirtual: false, IsAbstract: false, IsOverride: false } property when followsMutableState && Symbols.IsAutoProperty(property) => true,
        _ => null,
    };

    private bool SameStart(TrackedValue other) =>
        _start == other._start && SymbolEqualityComparer.Default.Equals(_startSymbol, other._startSymbol);

    private static TrackedValue Starting(Session session, Start start, ISymbol? symbol = null, bool isExternallyMutable = false) =>
        new(session, start, symbol, ImmutableArray<Step>.Empty, isExternallyMutable);

    private TrackedValue With(Step step, bool isExternallyMutable = false) =>
        new(Session, _start, _startSymbol, Steps.Add(step), IsExternallyMutable || isExternallyMutable);

    /// <summary>
    /// Any one element of this value, a collection.
    /// </summary>
    public TrackedValue Element(ITypeSymbol elementType) =>
        With(new ElementStep(elementType));

    public bool Equals(TrackedValue? other) =>
        other is not null && SameStart(other) && Steps.SequenceEqual(other.Steps);

    public override bool Equals(object? obj) =>
        Equals(obj as TrackedValue);

    /// <summary>
    /// The value <paramref name="operation"/> reads, if it's one the analysis can follow in <paramref name="session"/>.
    /// </summary>
    public static TrackedValue? From(IOperation? operation, Session session) =>
        From(operation, depth: 0, session);

    /// <summary>
    /// What a <c>?.</c> chain reads where its receivers aren't null:
    /// <c>owner.Pet</c> for <c>owner?.Pet</c>.
    /// Only for tests:
    /// wherever the member is read afterwards, what it's read from isn't null, so a test of the chain says the same about it there.
    /// (As a switch input, the chain can also be null, which the member can't.)
    /// </summary>
    public static TrackedValue? FromNullConditional(IOperation? operation, Session session)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation is IConditionalAccessOperation access ? FromNullConditional(access.WhenNotNull, session) : From(operation, session);
    }

    public override int GetHashCode() =>
        Steps.Aggregate(((int)_start * 31) + SymbolEqualityComparer.Default.GetHashCode(_startSymbol), (hash, step) => (hash * 31) + step.GetHashCode());

    /// <summary>
    /// Whether this is <paramref name="other"/> or something it's read from:
    /// <c>owner</c> for <c>owner.Pet</c>.
    /// </summary>
    public bool IsPrefixOf(TrackedValue other) =>
        SameStart(other) && Steps.Length <= other.Steps.Length && Steps.SequenceEqual(other.Steps.Take(Steps.Length));

    /// <summary>
    /// Whether <paramref name="operation"/> reads this value.
    /// </summary>
    public bool IsReadBy(IOperation? operation) =>
        _start == Start.StructThisInHelper
            ? Steps.IsEmpty && operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } self
                && SymbolEqualityComparer.Default.Equals(self.Type, _startSymbol)
            : From(operation, Session) is { } other && Equals(other);

    /// <summary>
    /// Whether a member of an object always reads the same once the object is constructed, as <see cref="WithMember"/> requires.
    /// </summary>
    public static bool IsStableMember(ISymbol member, Compilation? compilation, Session session) =>
        !member.IsStatic && ReadsMutableState(member, compilation, depth: 0, followsMutableState: false, session) == false;

    /// <summary>
    /// A local or parameter, with no steps.
    /// </summary>
    public static TrackedValue OfVariable(ISymbol variable, Session session) =>
        Starting(session, Start.Variable, variable);

    /// <summary>
    /// A local or parameter with steps read from it, which the caller knows can be followed.
    /// </summary>
    public static TrackedValue Path(ISymbol variable, IEnumerable<Step> steps, Session session) =>
        new(session, Start.Variable, variable, [.. steps], isExternallyMutable: false);

    /// <summary>
    /// This value, as reached from <paramref name="replacement"/> instead of from <paramref name="prefix"/>, which it starts with:
    /// <c>p.Pet</c> for <c>owner.Pet</c>, with <c>owner</c> passed as the parameter <c>p</c>.
    /// </summary>
    public TrackedValue Rebased(TrackedValue prefix, TrackedValue replacement) =>
        new(Session, replacement._start, replacement._startSymbol, replacement.Steps.AddRange(Steps.Skip(prefix.Steps.Length)), IsExternallyMutable || replacement.IsExternallyMutable);

    /// <summary>
    /// <c>this</c> inside a member of <paramref name="type"/>, for reading a helper member's body as though it were inline.
    /// In a class that's the usual <c>this</c>;
    /// in a struct (a union's own members) it's only ever compared with <c>this</c> in that body, never followed.
    /// </summary>
    public static TrackedValue ThisOf(ITypeSymbol type, Session session) =>
        type.IsReferenceType ? Starting(session, Start.This) : Starting(session, Start.StructThisInHelper, type);

    /// <summary>
    /// This value with more steps read from it, each member one followed as reading it in code would be (see <see cref="ReadsMutableState"/>), or null if one of them can't be.
    /// </summary>
    public TrackedValue? Through(IEnumerable<Step> steps, Compilation compilation, bool followsMutableState)
    {
        var value = this;
        foreach (var step in steps)
        {
            if (step is MemberStep { Member: var member })
            {
                if (member.IsStatic || ReadsMutableState(member, compilation, depth: 0, followsMutableState, Session) is not { } isExternallyMutable)
                {
                    return null;
                }

                value = value.With(step, isExternallyMutable);
            }
            else
            {
                value = value.With(step);
            }
        }

        return value;
    }

    public override string ToString()
    {
        var start = _start switch
        {
            Start.Variable or Start.StructThisInHelper => _startSymbol!.Name,
            Start.Static when Steps.FirstOrDefault() is MemberStep first => first.Member.ContainingType.Name,
            _ => "this",
        };
        
        return start + string.Concat(Steps.Select(step => step is MemberStep ? "." + step : step.ToString()));
    }

    /// <summary>
    /// This value's member, if it always reads the same (see <see cref="IsStableMember"/>).
    /// </summary>
    public TrackedValue? WithMember(ISymbol member, Compilation? compilation) =>
        IsStableMember(member, compilation, Session) ? With(new MemberStep(member)) : null;

    /// <summary>
    /// This value, read from <paramref name="root"/> rather than from where it starts:
    /// what <c>local.Pet</c> is after <c>local = owner</c>.
    /// </summary>
    public TrackedValue WithRoot(TrackedValue root) =>
        new(Session, root._start, root._startSymbol, root.Steps.AddRange(Steps), IsExternallyMutable || root.IsExternallyMutable);
}

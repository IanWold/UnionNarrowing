using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace UnionNarrowing.Suppressors.Analysis;

file class Element(int root, IReadOnlyList<Step> path, ITypeSymbol type, CaseSpace space, IReadOnlyList<(int Parent, ITypeSymbol Case)> requires)
{
    public IReadOnlyList<Step> Path =>
        path;

    /// <summary>
    /// The cases the element's ancestors have to hold for it to be there.
    /// </summary>
    public IReadOnlyList<(int Parent, ITypeSymbol Case)> Requires =>
        requires;

    /// <summary>
    /// Which of the switch's inputs (a tuple literal's elements) the path starts from.
    /// </summary>
    public int Root =>
        root;

    public CaseSpace Space =>
        space;

    /// <summary>
    /// The element's static type:
    /// a step into it as another type narrows it.
    /// </summary>
    public ITypeSymbol Type =>
        type;
}

/// <summary>
/// The places a switch's patterns look at, with what earlier code rules out at each, and what patterns say about a combination of their cases (the one <see cref="Enumerate"/> is visiting).
/// </summary>
file class Places
{
    private object?[] _combination = [];
    private int _count;
    private readonly CancellationToken _ct;
    private readonly List<Element> _elements = [];
    private List<CaseSet> _excluded = [];

    /// <summary>
    /// Combinations of the cases of the elements the analysis follows (by index) that earlier code rules out together (<c>if (b is { Left: Lit, Right: Lit }) return;</c>), as boxes over those elements.
    /// </summary>
    private (List<int> Elements, IReadOnlyList<CaseSet[]> Boxes) _together = ([], []);

    /// <summary>
    /// Where the patterns take an object apart positionally (<c>Marker(true)</c>), the members each position reads, by where the object is.
    /// The compiler only counts arms as covering an object together when they read its members the same way.
    /// </summary>
    private readonly List<(int Root, IReadOnlyList<Step> Prefix, IReadOnlyList<ISymbol?> Members)> _positions = [];
    private readonly ImmutableArray<IOperation> _inputs;
    private readonly bool _isTuple;
    private readonly SemanticModel _model;

    /// <summary>
    /// The "case" of an element that isn't there in a combination.
    /// </summary>
    private static readonly object NotThere = new();

    /// <summary>
    /// What each pattern says about the cases of the element it's on, by pattern and element, which doesn't depend on the combination being visited.
    /// </summary>
    private readonly Dictionary<(IPatternOperation, int), Outcome> _outcomes = [];

    private bool _stopped;
    private readonly SyntaxNode _switchNode;

    private Compilation Compilation =>
        _model.Compilation;

    private Outcome OutcomeAt(IPatternOperation pattern, int at)
    {
        if (!_outcomes.TryGetValue((pattern, at), out var outcome))
        {
            _outcomes[(pattern, at)] = outcome = Facts.Of(pattern, _elements[at].Space, Compilation);
        }

        return outcome;
    }

    /// <summary>
    /// The combination's cases of the inputs themselves, without what's inside them.
    /// </summary>
    public object?[] InputCases =>
        [.. _combination.Take(_inputs.Length)];

    /// <summary>
    /// The case spaces of the inputs themselves.
    /// </summary>
    public IReadOnlyList<CaseSpace> InputSpaces =>
        [.. _elements.Take(_inputs.Length).Select(element => element.Space)];

    private Places(IOperation input, SyntaxNode switchNode, SemanticModel model, CancellationToken ct)
    {
        _inputs = input is ITupleOperation tuple ? tuple.Elements : [input];
        _isTuple = input is ITupleOperation;
        _switchNode = switchNode;
        _model = model;
        _ct = ct;
    }

    /// <summary>
    /// Whether the pattern, at the place <paramref name="path"/> leads to, matches every value with the combination's cases.
    /// </summary>
    private bool Always(IPatternOperation pattern, int root, IReadOnlyList<Step> path)
    {
        var at = Find(root, path);
        var candidate = at >= 0 ? _combination[at] : NotThere;

        switch (pattern)
        {
            case IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }:
                return true;

            case ITypePatternOperation or IDeclarationPatternOperation or IConstantPatternOperation or IRelationalPatternOperation:
                return at >= 0 && candidate != NotThere && OutcomeAt(pattern, at).AlwaysTrue.ContainsCase(candidate);

            // At a place that isn't an element (a member that's neither a union nor a closed type, and can't be null), only the
            // subpatterns matter, if the type test always holds there.
            case IRecursivePatternOperation recursive:
                var holds = at < 0
                    ? path.Count > 0 && DeclaredType(path[path.Count - 1]) is { } memberType
                        && memberType is not { IsReferenceType: true, NullableAnnotation: not NullableAnnotation.NotAnnotated }
                        && Symbols.UnderlyingOfNullable(memberType) is null
                        && Facts.CaseIsAlways(memberType, recursive.MatchedType, _elements[root].Space, Compilation)
                    : candidate is not null && candidate != NotThere && Facts.CaseIsAlways(candidate, recursive.MatchedType, _elements[at].Space, Compilation);
                if (!holds)
                {
                    return false;
                }

                var inner = Into(recursive.MatchedType, root, path);
                for (var i = 0; i < recursive.DeconstructionSubpatterns.Length; i++)
                {
                    var sub = recursive.DeconstructionSubpatterns[i];
                    if (!(Facts.PositionalMember(recursive, i) is { } member ? Always(sub, root, [.. inner, new MemberStep(member)]) : Facts.MatchesAnything(sub)))
                    {
                        return false;
                    }
                }

                return recursive.PropertySubpatterns.All(property =>
                    Facts.MemberChain(property.Member) is { Count: > 0 } chain ? Always(property.Pattern, root, [.. inner, .. chain]) : Facts.MatchesAnything(property.Pattern)
                );

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                return Always(and.LeftPattern, root, path) && Always(and.RightPattern, root, RightPath(and, root, path));

            case IBinaryPatternOperation or:
                return Always(or.LeftPattern, root, path) || Always(or.RightPattern, root, path);

            case INegatedPatternOperation not:
                return !Could(not.Pattern, root, path);

            default:
                return false;
        }
    }

    /// <summary>
    /// The elements read through members (only) from <paramref name="prefix"/>, with the members they're read through.
    /// </summary>
    private List<(int Index, IReadOnlyList<MemberStep> Chain)> Children(Element parent, IReadOnlyList<Step> prefix)
    {
        var children = new List<(int, IReadOnlyList<MemberStep>)>();
        for (var j = 0; j < _elements.Count; j++)
        {
            var child = _elements[j];
            if (child.Root != parent.Root || child.Path.Count <= prefix.Count || _combination[j] == NotThere || !child.Path.Take(prefix.Count).SequenceEqual(prefix))
            {
                continue;
            }

            var chain = child.Path.Skip(prefix.Count).ToList();
            if (chain.All(step => step is MemberStep))
            {
                children.Add((j, chain.Cast<MemberStep>().ToList()));
            }
        }

        return children;
    }

    /// <summary>
    /// A pattern for what's at <paramref name="prefix"/> (of type <paramref name="name"/>, if given) with the patterns for the elements inside it:
    /// positional (<c>Marker(false)</c>) where the arms take it apart that way and each element is one of its positions, or else <c>{ Member: pattern }</c>.
    /// </summary>
    private PatternSyntax WithMembers(TypeSyntax? name, Element parent, IReadOnlyList<Step> prefix)
    {
        var children = Children(parent, prefix);
        if (children.Count == 0 && name is not null)
        {
            return TypePattern(name);
        }

        if (name is not null
            && _positions.FirstOrDefault(p => p.Root == parent.Root && p.Prefix.SequenceEqual(prefix)).Members is { } positions
            && children.All(child => child.Chain.Count == 1 && positions.Contains(child.Chain[0].Member, SymbolEqualityComparer.Default))
        )
        {
            var subpatterns = positions.Select(position => children.FirstOrDefault(child => SymbolEqualityComparer.Default.Equals(child.Chain[0].Member, position)) is { Chain: not null } child ? Write(child.Index) : DiscardPattern());
            return RecursivePattern(name, PositionalPatternClause(SeparatedList(subpatterns.Select(Subpattern))), null, null);
        }

        return RecursivePattern(name, null, PropertyPatternClause(SeparatedList(children.Select(child =>
        {
            ExpressionSyntax member = IdentifierName(child.Chain[0].Member.Name);
            foreach (var next in child.Chain.Skip(1))
            {
                member = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, member, IdentifierName(next.Member.Name));
            }

            return Subpattern(child.Chain.Count == 1 ? NameColon(IdentifierName(child.Chain[0].Member.Name)) : ExpressionColon(member, Token(SyntaxKind.ColonToken)), Write(child.Index));
        }))), null);
    }

    /// <summary>
    /// Collects where the pattern looks:
    /// every place it tests that's a union or closed hierarchy, and every bool, or member that can be null, which arms can split between them (<c>Marker(true)</c> and <c>Marker(false)</c>, <c>{ Frame: null }</c> and <c>{ Frame: { } f }</c>).
    /// </summary>
    private void Collect(IPatternOperation pattern, int root, IReadOnlyList<Step> path)
    {
        if (path.Count(step => step is MemberStep) > Limits.NestedDepth || _elements.Count >= Limits.NestedPlaces)
        {
            return;
        }

        if (path.Count > 0 && Find(root, path) < 0
            && CaseSpace.OfElement(PlaceType(pattern, path), _switchNode.SpanStart, _model, _ct) is { } space
            && (space.IsOfTypes || space.Kind == SpaceKind.Bool || (space.Kind == SpaceKind.Opaque && space.NullMatters))
        )
        {
            var requires = new List<(int, ITypeSymbol)>();
            for (var k = 0; k < path.Count; k++)
            {
                if (path[k] is CaseStep { Type: var step } && Find(root, [.. path.Take(k)]) is var parent and >= 0)
                {
                    requires.Add((parent, step));
                }
            }

            _elements.Add(new Element(root, path, pattern.InputType, space, requires));
        }

        switch (pattern)
        {
            case IRecursivePatternOperation recursive:
                var inner = Into(recursive.MatchedType, root, path);
                if (recursive.DeconstructionSubpatterns.Length > 0 && !_positions.Any(p => p.Root == root && p.Prefix.SequenceEqual(inner)))
                {
                    _positions.Add((root, inner, [.. recursive.DeconstructionSubpatterns.Select((_, i) => Facts.PositionalMember(recursive, i))]));
                }

                for (var i = 0; i < recursive.DeconstructionSubpatterns.Length; i++)
                {
                    if (Facts.PositionalMember(recursive, i) is { } member)
                    {
                        Collect(recursive.DeconstructionSubpatterns[i], root, [.. inner, new MemberStep(member)]);
                    }
                }

                foreach (var property in recursive.PropertySubpatterns)
                {
                    if (Facts.MemberChain(property.Member) is { Count: > 0 } chain)
                    {
                        Collect(property.Pattern, root, [.. inner, .. chain]);
                    }
                }

                break;

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                Collect(and.LeftPattern, root, path);
                Collect(and.RightPattern, root, RightPath(and, root, path));
                break;

            case IBinaryPatternOperation or:
                Collect(or.LeftPattern, root, path);
                Collect(or.RightPattern, root, path);
                break;

            case INegatedPatternOperation not:
                Collect(not.Pattern, root, path);
                break;
        }
    }

    /// <summary>
    /// The type of the place a pattern looks at:
    /// a member's declared type, which says whether it can be null as the compiler goes by it (the pattern's input type has no nullable annotation), or else the pattern's input type.
    /// </summary>
    private static ITypeSymbol PlaceType(IPatternOperation pattern, IReadOnlyList<Step> path) =>
        (path.Count == 0 ? null : DeclaredType(path[path.Count - 1])) ?? pattern.InputType;

    /// <summary>
    /// The declared type of the member a step reads, if it reads one.
    /// </summary>
    private static ITypeSymbol? DeclaredType(Step step) => step switch
    {
        MemberStep { Member: IFieldSymbol field } => field.Type,
        MemberStep { Member: IPropertySymbol property } => property.Type,
        _ => null,
    };

    /// <summary>
    /// Collects the places a pattern looks at:
    /// on a tuple literal, its arms are tuple patterns, element by element;
    /// anything else is a pattern on the one input.
    /// </summary>
    private void CollectTop(IPatternOperation pattern)
    {
        if (!_isTuple)
        {
            Collect(pattern, 0, []);
            return;
        }

        switch (pattern)
        {
            case IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == _inputs.Length:
                for (var i = 0; i < _inputs.Length; i++)
                {
                    Collect(recursive.DeconstructionSubpatterns[i], i, []);
                }

                break;

            case IBinaryPatternOperation binary:
                CollectTop(binary.LeftPattern);
                CollectTop(binary.RightPattern);
                break;

            case INegatedPatternOperation not:
                CollectTop(not.Pattern);
                break;
        }
    }

    /// <summary>
    /// Whether the pattern, at the place <paramref name="path"/> leads to, could match some value with the combination's cases.
    /// </summary>
    private bool Could(IPatternOperation pattern, int root, IReadOnlyList<Step> path)
    {
        var at = Find(root, path);
        var candidate = at >= 0 ? _combination[at] : NotThere;

        switch (pattern)
        {
            case IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }:
                return true;

            case ITypePatternOperation or IDeclarationPatternOperation or IConstantPatternOperation or IRelationalPatternOperation:
                return at < 0 || candidate == NotThere || !OutcomeAt(pattern, at).AlwaysFalse.ContainsCase(candidate);

            case IRecursivePatternOperation recursive:
                if (at >= 0 && candidate != NotThere && (candidate is null || OutcomeAt(pattern, at).AlwaysFalse.ContainsCase(candidate)))
                {
                    return false;
                }

                var inner = Into(recursive.MatchedType, root, path);
                for (var i = 0; i < recursive.DeconstructionSubpatterns.Length; i++)
                {
                    if (Facts.PositionalMember(recursive, i) is { } member && !Could(recursive.DeconstructionSubpatterns[i], root, [.. inner, new MemberStep(member)]))
                    {
                        return false;
                    }
                }

                return recursive.PropertySubpatterns.All(property =>
                    Facts.MemberChain(property.Member) is not { Count: > 0 } chain || Could(property.Pattern, root, [.. inner, .. chain])
                );

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                return Could(and.LeftPattern, root, path) && Could(and.RightPattern, root, RightPath(and, root, path));

            case IBinaryPatternOperation or:
                return Could(or.LeftPattern, root, path) || Could(or.RightPattern, root, path);

            case INegatedPatternOperation not:
                return !Always(not.Pattern, root, path);

            default:
                return true;
        }
    }

    private void Enumerate(int index, bool withNull, Func<bool> visit)
    {
        if (_stopped || _count > Limits.Combinations)
        {
            return;
        }

        if (index == _elements.Count)
        {
            _count++;
            _stopped = !visit();
            return;
        }

        // There only when each ancestor it steps through holds a case that's always of the type it steps in as.
        var element = _elements[index];
        if (element.Requires.Any(r => _combination[r.Parent] is not ITypeSymbol held || !Facts.CaseIsAlways(held, r.Case, _elements[r.Parent].Space, Compilation)))
        {
            _combination[index] = NotThere;
            Enumerate(index + 1, withNull, visit);
            return;
        }

        foreach (var candidate in element.Space.Cases.Cast<object?>().Concat(element.Space.NullMatters || withNull ? [null] : []))
        {
            _combination[index] = candidate;
            Enumerate(index + 1, withNull, visit);
        }
    }

    /// <summary>
    /// The element <paramref name="path"/> leads to from input <paramref name="root"/>, or -1.
    /// </summary>
    private int Find(int root, IReadOnlyList<Step> path) =>
        _elements.FindIndex(element => element.Root == root && element.Path.SequenceEqual(path));

    /// <summary>
    /// The path to a case's members, which are read from the case it holds (for a union) or the object as that type (for a closed hierarchy):
    /// a step into it, as that type.
    /// </summary>
    private IReadOnlyList<Step> Into(ITypeSymbol type, int root, IReadOnlyList<Step> path) =>
        Find(root, path) is var at and >= 0 && _elements[at].Space.IsOfTypes
            && !SymbolEqualityComparer.Default.Equals(type, _elements[at].Space.UnionType ?? _elements[at].Type)
            && !SymbolEqualityComparer.Default.Equals(type, Symbols.UnderlyingOfNullable(_elements[at].Type))
            ? [.. path, new CaseStep(type)]
            : path;

    /// <summary>
    /// The path the right of an <c>and</c> tests:
    /// in <c>Pet and Bird</c> on a union, Bird tests what the Pet holds.
    /// </summary>
    private IReadOnlyList<Step> RightPath(IBinaryPatternOperation and, int root, IReadOnlyList<Step> path) =>
        SymbolEqualityComparer.Default.Equals(and.RightPattern.InputType, and.LeftPattern.NarrowedType)
            ? Into(and.LeftPattern.NarrowedType, root, path)
            : path;

    /// <summary>
    /// The combination's case for an element, as a pattern:
    /// its type, with what's inside it written out the way the arms look at it (a step into the union it holds, members).
    /// </summary>
    private PatternSyntax Write(int index)
    {
        var element = _elements[index];
        var candidate = _combination[index];

        // Any value, where null is a case of its own: `{ }`.
        if (candidate is not ITypeSymbol type)
        {
            return candidate == CaseSpace.Anything && (Children(element, element.Path).Count > 0 || element.Space.NullMatters)
                ? WithMembers(null, element, element.Path)
                : MissingPatterns.Of(candidate == NotThere ? CaseSpace.Anything : candidate, element.Space, _model, _switchNode.SpanStart);
        }

        IReadOnlyList<Step> inside = element.Space.IsOfTypes ? [.. element.Path, new CaseStep(type)] : element.Path;
        var written = WithMembers(MissingPatterns.TypeName(type, _model, _switchNode.SpanStart), element, inside);

        // The union a case holds: `Pet and Bird`.
        var innerUnion = Find(element.Root, inside);

        return innerUnion >= 0 && _combination[innerUnion] != NotThere
            ? BinaryPattern(SyntaxKind.AndPattern, written, Write(innerUnion))
            : written;
    }

    /// <summary>
    /// Whether a pattern on the input is sure to match the combination.
    /// </summary>
    public bool AlwaysTop(IPatternOperation pattern) =>
        !_isTuple ? Always(pattern, 0, []) : pattern switch
        {
            IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => true,
            IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == _inputs.Length =>
                recursive.DeconstructionSubpatterns.Select((sub, i) => Always(sub, i, [])).All(x => x),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => AlwaysTop(and.LeftPattern) && AlwaysTop(and.RightPattern),
            IBinaryPatternOperation or => AlwaysTop(or.LeftPattern) || AlwaysTop(or.RightPattern),
            INegatedPatternOperation not => !CouldTop(not.Pattern),
            _ => false,
        };

    /// <summary>
    /// Whether a pattern on the input could match some value with the combination's cases.
    /// </summary>
    public bool CouldTop(IPatternOperation pattern) =>
        !_isTuple ? Could(pattern, 0, []) : pattern switch
        {
            IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == _inputs.Length =>
                recursive.DeconstructionSubpatterns.Select((sub, i) => Could(sub, i, [])).All(x => x),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => CouldTop(and.LeftPattern) && CouldTop(and.RightPattern),
            IBinaryPatternOperation or => CouldTop(or.LeftPattern) || CouldTop(or.RightPattern),
            INegatedPatternOperation not => !AlwaysTop(not.Pattern),
            _ => true,
        };

    /// <summary>
    /// Visits every combination of the elements' cases, until <paramref name="visit"/> says to stop.
    /// False if it stopped, and null if there were too many combinations to go through.
    /// </summary>
    /// <param name="withNull">Whether null is a case of every element, rather than only where it matters.</param>
    public bool? Enumerate(bool withNull, Func<bool> visit)
    {
        _combination = new object?[_elements.Count];
        _count = 0;
        _stopped = false;
        Enumerate(0, withNull, visit);
        return _count > Limits.Combinations ? null : !_stopped;
    }

    /// <summary>
    /// Whether one of the labels (none of which has a <c>when</c> clause) is sure to match the combination.
    /// </summary>
    public bool Handled(IEnumerable<SwitchLabel> unguarded) =>
        unguarded.Any(label => label.IsDefault || label.Pattern is { } pattern && AlwaysTop(pattern));

    /// <summary>
    /// The places <paramref name="patterns"/> look at, as well as the inputs themselves.
    /// Null if they don't look inside the input, or the input isn't one this can reason about.
    /// </summary>
    public static Places? Of(IOperation input, IEnumerable<IPatternOperation> patterns, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var places = new Places(input, switchNode, model, ct);
        foreach (var each in places._inputs)
        {
            if (each.Type is null || CaseSpace.OfElement(each, model, ct) is not { } rootSpace)
            {
                return null;
            }

            places._elements.Add(new Element(places._elements.Count, [], each.Type, rootSpace, []));
        }

        foreach (var pattern in patterns)
        {
            places.CollectTop(pattern);
        }

        if (places._elements.Count == places._inputs.Length)
        {
            return null;
        }

        var roots = places._inputs.Select(each => TrackedValue.From(each, session) is { } value && Narrowing.IsTrackable(value, switchNode, model, ct) ? value : null).ToList();
        var followsMutableState = session.FollowsMutableState(switchNode.SyntaxTree);
        var followed = places._elements
            .Select(e => roots[e.Root]?.Through(e.Path, model.Compilation, followsMutableState) is { } place && Narrowing.IsTrackable(place, switchNode, model, ct) ? place : null)
            .ToList();

        // At a place inside the input, a case is also ruled out where every case of one of its members is (the input's
        // own cases get that from SwitchCoverage).
        places._excluded = [.. followed.Select((place, i) => place is null ? CaseSet.Empty
            : Narrowing.Excluded(switchNode, place, places._elements[i].Space, model, ct)
                | (i >= places._inputs.Length && places._elements[i].Space.IsOfTypes
                    ? SwitchCoverage.RuledOutInside(place, places._elements[i].Space, places._elements[i].Space.Cases.OfType<ITypeSymbol>(), switchNode, model, session, ct)
                    : CaseSet.Empty
                )
        )];

        var together = Enumerable.Range(0, followed.Count).Where(i => followed[i] is not null).ToList();
        if (together.Count > 1)
        {
            places._together = (together, Narrowing.ExcludedTogether(switchNode, [.. together.Select(i => followed[i]!)], [.. together.Select(i => places._elements[i].Space)], model, ct));
        }

        return places;
    }

    /// <summary>
    /// Whether earlier code ruled out the combination:
    /// one of its elements' cases.
    /// </summary>
    /// <summary>
    /// Whether earlier code rules out nothing at any of the places, on its own or together with others.
    /// </summary>
    public bool NothingRuledOut =>
        _excluded.All(excluded => excluded.IsEmpty) && _together.Boxes.Count == 0;

    public bool RuledOut() =>
        _combination.Select((candidate, i) => candidate != NotThere && _excluded[i].ContainsCase(candidate)).Any(x => x)
        || _together.Boxes.Any(box => _together.Elements.Select((element, j) => _combination[element] != NotThere && box[j].ContainsCase(_combination[element])).All(x => x));

    /// <summary>
    /// The combination, as a pattern on the input.
    /// </summary>
    public PatternSyntax Written()
    {
        var written = (_isTuple ? RecursivePattern(null, PositionalPatternClause(SeparatedList(_inputs.Select((_, root) => Subpattern(Write(root))))), null, null) : Write(0)).NormalizeWhitespace();

        // `Marker(false)`, not `Marker (false)`.
        return written.ReplaceNodes(
            written.DescendantNodesAndSelf().OfType<RecursivePatternSyntax>().Where(recursive => recursive is { Type: not null, PositionalPatternClause: not null }),
            (_, recursive) => recursive.WithType(recursive.Type!.WithoutTrailingTrivia())
        );
    }
}


/// <summary>
/// Whether a switch handles every value of its input when its arms look inside it:
/// at the union a union's case holds (<c>Pet and (Cat or Dog)</c> on a union of Pets and Rocks), at a union-typed member of a case (<c>Kitten { Toy: Mouse }</c>), or at a union-typed member of the object switched on (<c>{ Pet: Cat }</c>).
/// </summary>
/// <remarks>
/// Each place the arms look at becomes an element, with its own cases, alongside the input:
/// a path from the input through members, and through the type of the case a union holds (an element under a union's case is only there when the union holds that case, and is "not there" otherwise).
/// Every combination of the elements' cases has to be one an arm without a <c>when</c> clause is sure to match, or include a case earlier code ruled out for its element.
/// </remarks>
internal static class NestedCoverage
{
    /// <summary>
    /// The arms (or labels, by index into <paramref name="labels"/>) that look inside the input and can't match:
    /// every combination of cases they could match was ruled out by earlier code, or is one an earlier arm without a <c>when</c> clause is sure to match.
    /// Null is a case of every element here, wherever the compiler would want it handled or not, since removing an arm mustn't change what happens to any value.
    /// Null if the arms don't look inside the input.
    /// Arms no combination could reach at all aren't included:
    /// the compiler reports those.
    /// </summary>
    public static List<int>? DeadArms(IOperation input, IReadOnlyList<SwitchLabel> labels, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var patterns = labels.Select(label => label.Pattern).ToList();
        if (Places.Of(input, patterns.OfType<IPatternOperation>(), switchNode, model, session, ct) is not { } places)
        {
            return null;
        }

        if (places.NothingRuledOut)
        {
            return [];
        }

        var possible = new bool[labels.Count];
        var alive = new bool[labels.Count];
        var finished = places.Enumerate(withNull: true, () =>
        {
            var ruledOut = places.RuledOut();
            var taken = false;

            for (var i = 0; i < labels.Count; i++)
            {
                if (patterns[i] is { } candidate && !taken && places.CouldTop(candidate))
                {
                    possible[i] = true;
                    alive[i] |= !ruledOut;
                }

                taken |= labels[i].Guard is null && (labels[i].IsDefault || patterns[i] is { } earlier && places.AlwaysTop(earlier));
            }

            return Enumerable.Range(0, labels.Count).Any(i => patterns[i] is not null && !alive[i]);
        });

        return finished is null
            ? null
            : [.. Enumerable.Range(0, labels.Count).Where(i => patterns[i] is not null && possible[i] && !alive[i])];
    }

    /// <summary>
    /// Patterns for the combinations the arms miss, written to look inside the way the arms do (<c>Kitten { Toy: Ball }</c>, <c>Pet and Bird</c>, <c>{ Pet: Bird }</c>).
    /// Past <see cref="Limits.WrittenOutMissing"/> of them, patterns for the inputs' cases they're in instead, as <see cref="MissingPatterns"/> writes them (<c>Kitten</c>).
    /// Null if the arms don't look inside.
    /// </summary>
    public static List<PatternSyntax>? Missing(IOperation input, IReadOnlyList<SwitchLabel> labels, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct) =>
        Left(input, labels, switchNode, model, session, ct).Missing;

    /// <summary>
    /// Patterns for the combinations the arms miss (see <see cref="Missing"/>), and whether earlier code rules out others they don't handle.
    /// </summary>
    public static (List<PatternSyntax>? Missing, bool RuledOut) Left(IOperation input, IReadOnlyList<SwitchLabel> labels, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var unguarded = labels.Where(label => label.Guard is null).ToList();
        if (Places.Of(input, unguarded.Select(label => label.Pattern).OfType<IPatternOperation>(), switchNode, model, session, ct) is not { } places)
        {
            return (null, false);
        }

        var missing = new List<PatternSyntax>();
        var inputCases = new List<object?[]>();
        var ruledOut = false;
        var finished = places.Enumerate(withNull: false, () =>
        {
            if (places.Handled(unguarded))
            {
                return true;
            }

            if (places.RuledOut())
            {
                ruledOut = true;
                return true;
            }

            if (missing.Count <= Limits.WrittenOutMissing && places.Written() is var written && !missing.Any(m => m.IsEquivalentTo(written)))
            {
                missing.Add(written);
            }

            if (places.InputCases is var cases && !inputCases.Any(seen => seen.Zip(cases, CaseComparer.Instance.Equals).All(same => same)))
            {
                inputCases.Add(cases);
            }

            return true;
        });

        var written = finished is null || missing.Count == 0 ? null
            : missing.Count <= Limits.WrittenOutMissing ? missing
            : MissingPatterns.Of(places.InputSpaces, inputCases, model, switchNode.SpanStart);
        return (written, ruledOut && finished is not null);
    }

    /// <summary>
    /// Whether the arms (and labels) cover every value, with what earlier code rules out, and whether that was needed.
    /// Null if the arms don't look inside the input, or the input isn't one this can reason about.
    /// </summary>
    public static (bool Complete, bool ThanksToEarlierCode)? Of(IOperation input, IReadOnlyList<SwitchLabel> labels, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var unguarded = labels.Where(label => label.Guard is null).ToList();
        if (Places.Of(input, unguarded.Select(label => label.Pattern).OfType<IPatternOperation>(), switchNode, model, session, ct) is not { } places)
        {
            return null;
        }

        var thanks = false;
        var complete = places.Enumerate(withNull: false, () =>
        {
            if (places.Handled(unguarded))
            {
                return true;
            }

            if (!places.RuledOut())
            {
                return false;
            }

            thanks = true;
            return true;
        });

        return complete is { } finished
            ? (finished, thanks && finished)
            : null;
    }
}

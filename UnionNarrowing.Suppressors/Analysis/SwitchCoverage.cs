using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What a switch over a union or closed hierarchy (or a tuple with one in it) handles, given what earlier code rules out:
/// whether that's every case, and the combinations of cases it misses.
/// </summary>
internal class SwitchCoverage
{
    /// <summary>
    /// One value a switch is over:
    /// its whole input, or an element of a tuple.
    /// </summary>
    /// <param name="value">The value, if the analysis can follow it to the switch.</param>
    private readonly struct Input(IOperation? operation, TrackedValue? value, CaseSpace space)
    {
        public IOperation? Operation { get; } = operation;

        public CaseSpace Space { get; } = space;

        public TrackedValue? Value { get; } = value;
    }

    /// <summary>
    /// A switch that isn't over cases, but whose arms look at unions inside its input and cover them.
    /// </summary>
    private static readonly SwitchCoverage CompleteInside = new(overCases: false, complete: true, thanksToEarlierCode: true, [], []);

    private static readonly SwitchCoverage None = new(false, false, false, [], []);

    /// <summary>
    /// Whether every combination of cases is handled or ruled out.
    /// </summary>
    public bool Complete { get; }

    /// <summary>
    /// The combinations of cases (one per input, null for null) that are neither handled nor ruled out.
    /// </summary>
    public IReadOnlyList<object?[]> Missing { get; }

    /// <summary>
    /// Whether the switch is over a union or closed type (or a tuple with one in it), which the analysis reasons about.
    /// </summary>
    public bool OverCases { get; }

    /// <summary>
    /// The case space of each input.
    /// </summary>
    public IReadOnlyList<CaseSpace> Spaces { get; }

    /// <summary>
    /// Whether the analysis (earlier code, or <c>when</c> clauses) was needed for that, beyond what the compiler sees.
    /// </summary>
    public bool ThanksToEarlierCode { get; }

    private SwitchCoverage(bool overCases, bool complete, bool thanksToEarlierCode, IReadOnlyList<CaseSpace> spaces, IReadOnlyList<object?[]> missing)
    {
        OverCases = overCases;
        Complete = complete;
        ThanksToEarlierCode = thanksToEarlierCode;
        Spaces = spaces;
        Missing = missing;
    }

    /// <summary>
    /// The combinations of cases an arm's pattern, or a case label, matches for every value, as boxes:
    /// one set of cases per input.
    /// </summary>
    private static IEnumerable<CaseSet[]> Boxes(IOperation patternOrClause, IReadOnlyList<CaseSpace> spaces, Compilation compilation)
    {
        var everything = spaces.Select(space => space.All).ToArray();
        return patternOrClause switch
        {
            IDefaultCaseClauseOperation => [everything],
            IPatternCaseClauseOperation clause => Boxes(clause.Pattern, spaces, compilation),
            ICaseClauseOperation clause when spaces.Count == 1 => [[Facts.Of(clause, spaces[0], compilation).AlwaysTrue]],
            IPatternOperation pattern when spaces.Count == 1 => [[Facts.Of(pattern, spaces[0], compilation).AlwaysTrue]],
            IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true } => [everything],
            // `(P, Q, ...)`, element by element.
            IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == spaces.Count =>
                [[.. recursive.DeconstructionSubpatterns.Select((element, i) => Facts.Of(element, spaces[i], compilation).AlwaysTrue)]],
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => Boxes(or.LeftPattern, spaces, compilation).Concat(Boxes(or.RightPattern, spaces, compilation)),
            _ => [],
        };
    }

    /// <summary>
    /// What's ruled out for an input the analysis can't follow as a whole, but whose parts it can:
    /// <c>(flag ? a : b) switch</c> and <c>(maybe ?? fallback) switch</c> are ruled out where both sides are.
    /// </summary>
    private static CaseSet ExcludedFor(IOperation operation, CaseSpace space, SemanticModel model, Session session, CancellationToken ct, int depth = 0)
    {
        while (operation is IConversionOperation { Conversion.IsIdentity: true, OperatorMethod: null } conversion)
        {
            operation = conversion.Operand;
        }

        if (depth > Limits.InputDepth)
        {
            return CaseSet.Empty;
        }

        // A branch that throws gives no value: `(flag ? a : throw ...)` is what `a` is, and `(x ?? throw ...)` is x, and
        // isn't null (where null can only mean nothing there).
        CaseSet? Branch(IOperation branch) =>
            Facts.IsThrow(branch) ? null : ExcludedFor(branch, space, model, session, ct, depth + 1);

        CaseSet Both(CaseSet? left, CaseSet? right) =>
            left is null ? right ?? CaseSet.Empty : right is null ? left : left & right;

        return operation switch
        {
            IConditionalOperation { WhenFalse: { } whenFalse } conditional => Both(Branch(conditional.WhenTrue), Branch(whenFalse)),
            ICoalesceOperation coalesce when Facts.IsThrow(coalesce.WhenNull) =>
                ExcludedFor(coalesce.Value, space, model, session, ct, depth + 1) | (space.NullOnlyAsNoValue ? CaseSet.Null : CaseSet.Empty),
            ICoalesceOperation coalesce =>
                ExcludedFor(coalesce.Value, space, model, session, ct, depth + 1) & ExcludedFor(coalesce.WhenNull, space, model, session, ct, depth + 1),
            IConditionalAccessOperation access when TrackedValue.FromNullConditional(access, session) is { } member && Narrowing.IsTrackable(member, operation.Syntax, model, ct) =>
                CaseSet.Of(Narrowing.Excluded(operation.Syntax, member, space, model, ct).Cases), // `owner?.Pet`: as owner.Pet, but it can be null
            _ => Narrowing.KnownExcluded(operation, [], space, model, session, ct, copies: 0),
        };
    }

    /// <summary>
    /// What the switch handles as the compiler sees it, as boxes:
    /// the arms and labels without a <c>when</c> clause.
    /// </summary>
    private static List<CaseSet[]> Handled(IReadOnlyList<SwitchLabel> labels, IReadOnlyList<CaseSpace> spaces, Compilation compilation) =>
        [.. labels.Where(label => label.Guard is null).SelectMany(label => Boxes(label.PatternOrClause, spaces, compilation))];

    /// <summary>
    /// What a switch over one value handles that the compiler doesn't see, as boxes:
    /// an arm with a <c>when</c> clause handles the cases its pattern and clause both always hold for (<c>_ when pet is Dog</c>), and arms between them the cases their clauses hold for whatever the bools they test are (see <see cref="SplitByFlags"/>).
    /// Only up to a clause that could have changed the value since the switch read it.
    /// </summary>
    private static List<CaseSet[]> HandledByWhenClauses(IOperation input, TrackedValue value, CaseSpace space, IReadOnlyList<SwitchLabel> labels, SyntaxNode switchNode, SemanticModel model, CancellationToken ct)
    {
        var handled = new List<CaseSet[]>();
        var usable = new List<(IPatternOperation Pattern, IOperation Guard)>();

        foreach (var label in labels)
        {
            if (label.Guard is not { } guard)
            {
                continue;
            }

            if (WriteAnalysis.Writes(guard.Syntax, value, model, ct))
            {
                break;
            }

            if (label.Pattern is { } pattern)
            {
                usable.Add((pattern, guard));
                handled.Add([Facts.Of(pattern, space, model.Compilation).AlwaysTrue & Facts.OfGuard(input, pattern, guard, value, space, model).AlwaysTrue]);
            }
        }

        var unguarded =
            labels.Where(label => label.Guard is null).Select(label => label.PatternOrClause switch
            {
                IPatternOperation pattern => Facts.Of(pattern, space, model.Compilation).AlwaysTrue,
                IDefaultCaseClauseOperation => space.All,
                ICaseClauseOperation clause => Facts.Of(clause, space, model.Compilation).AlwaysTrue,
                _ => CaseSet.Empty,
            })
            .ToList();

        handled.AddRange(SplitByFlags(value, space, usable, unguarded, switchNode, model, ct));

        return handled;
    }

    /// <summary>
    /// The values the switch is over, or null if any isn't one the analysis reasons about:
    /// a union or closed type (or a bool or enum), or a tuple with one in it, either a tuple literal or a variable of a tuple type, whose elements are followed separately.
    /// </summary>
    private static List<Input>? Inputs(IOperation input, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        TrackedValue? Followed(TrackedValue? value) =>
            value is not null && Narrowing.IsTrackable(value, switchNode, model, ct) ? value : null;

        var inputs = new List<Input>();
        if (input is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (CaseSpace.OfElement(element, model, ct) is not { } space)
                {
                    return null;
                }

                inputs.Add(new Input(element, Followed(TrackedValue.From(element, session)), space));
            }

            return inputs;
        }

        // `var t = (a, b); ... t switch { (Cat, _) => ... }`
        if (input.Type is INamedTypeSymbol { IsTupleType: true } tupleType && Followed(TrackedValue.From(input, session)) is { } whole)
        {
            foreach (var field in tupleType.TupleElements)
            {
                if (CaseSpace.OfElement(field.Type, switchNode.SpanStart, model, ct) is not { } space)
                {
                    return null;
                }

                inputs.Add(new Input(null, Followed(whole.WithMember(field, model.Compilation)), space));
            }

            return inputs;
        }

        if (CaseSpace.OfInput(input, model, ct) is not { } inputSpace)
        {
            return null;
        }

        inputs.Add(new Input(input, Followed(TrackedValue.From(input, session)), inputSpace));

        return inputs;
    }

    /// <summary>
    /// What earlier code rules out for each input it can follow, and for a tuple, the combinations it rules out together (<c>if (a is Bird &amp;&amp; b is Bird) return;</c>).
    /// </summary>
    private static (List<CaseSet> Each, IReadOnlyList<CaseSet[]> Together) RuledOut(List<Input> inputs, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var each =
            inputs.Select(i => i.Value is { } v
                ? Narrowing.Excluded(switchNode, v, i.Space, model, ct)
                : i.Operation is { } operation ? ExcludedFor(operation, i.Space, model, session, ct) : CaseSet.Empty
            ).
            ToList();

        var values = inputs.Select(i => i.Value).ToList();

        IReadOnlyList<CaseSet[]> together = inputs.Count > 1 && values.All(v => v is not null)
            ? Narrowing.ExcludedTogether(switchNode, values!, [.. inputs.Select(i => i.Space)], model, ct)
            : [];

        return (each, together);
    }

    /// <summary>
    /// The <paramref name="cases"/> of a value that earlier code rules out by ruling out everything one of their members can be:
    /// after <c>if (r is Err { Error: Invalid }) return; if (r is Err { Error: Missing }) return;</c>, where an Error can only be Invalid or Missing, r isn't an Err.
    /// Only members that are unions or of a closed type, and that the analysis follows.
    /// </summary>
    private static CaseSet RuledOutInside(TrackedValue value, CaseSpace space, IEnumerable<ITypeSymbol> cases, SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct)
    {
        var followsMutableState = session.FollowsMutableState(switchNode.SyntaxTree);
        var ruledOut = cases.Where(caseType => caseType.GetMembers().Any(member =>
            member switch { IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field => field.Type, IPropertySymbol { IsStatic: false, IsIndexer: false } property => property.Type, _ => null } is { } memberType
            && CaseSpace.OfElement(memberType, switchNode.SpanStart, model, ct) is { IsOfTypes: true } memberSpace
            && value.Through([new CaseStep(caseType), new MemberStep(member)], model.Compilation, followsMutableState) is { } place
            && Narrowing.IsTrackable(place, switchNode, model, ct)
            && Narrowing.Excluded(switchNode, place, memberSpace, model, ct) is var excluded
            && memberSpace.Cases.All(excluded.Contains) && (!memberSpace.NullMatters || excluded.IncludesNull)
        ));

        return CaseSet.Of(ruledOut);
    }

    /// <summary>
    /// The cases that <c>when</c> clauses handle between them, over the values of the bools they test:
    /// <c>Cat when loud =&gt; 1, Cat when !loud =&gt; 2</c> handles Cat, whatever loud is.
    /// A bool counts if it's a local or by-value parameter the analysis can follow, and no clause writes it.
    /// Each case comes back as a box of its own.
    /// </summary>
    /// <param name="unguarded">What each label without a <c>when</c> clause is sure to match.</param>
    private static IEnumerable<CaseSet[]> SplitByFlags(
        TrackedValue value, CaseSpace space, IReadOnlyList<(IPatternOperation Pattern, IOperation Guard)> guarded,
        IReadOnlyList<CaseSet> unguarded, SyntaxNode switchNode, SemanticModel model, CancellationToken ct
    )
    {
        if (guarded.Count < 2)
        {
            return [];
        }

        var flags = new List<TrackedValue>();
        foreach (var reference in guarded.SelectMany(g => g.Guard.DescendantsAndSelf()))
        {
            var flag = reference switch
            {
                ILocalReferenceOperation { Local: { Type.SpecialType: SpecialType.System_Boolean, IsRef: false } local } => TrackedValue.OfVariable(local, value.Session),
                IParameterReferenceOperation { Parameter: { Type.SpecialType: SpecialType.System_Boolean, RefKind: RefKind.None } parameter } => TrackedValue.OfVariable(parameter, value.Session),
                _ => null,
            };

            if (flag is null || flags.Contains(flag))
            {
                continue;
            }

            if (flags.Count == Limits.Flags || !Narrowing.IsTrackable(flag, switchNode, model, ct) || guarded.Any(g => WriteAnalysis.Writes(g.Guard.Syntax, flag, model, ct)))
            {
                return [];
            }

            flags.Add(flag);
        }

        if (flags.Count == 0 || CaseSpace.OfElement(model.Compilation.GetSpecialType(SpecialType.System_Boolean), switchNode.SpanStart, model, ct) is not { } boolSpace)
        {
            return [];
        }

        // What each arm is sure to match, over the value and the bools together.
        var values = new List<TrackedValue> { value };

        values.AddRange(flags);

        var spaces = values.Select((_, i) => i == 0 ? space : boolSpace).ToList();
        var boxes = unguarded.Select(matched => spaces.Select((s, i) => i == 0 ? matched : s.All).ToArray()).ToList();

        foreach (var (pattern, guard) in guarded)
        {
            var matched = Facts.Of(pattern, space, model.Compilation).AlwaysTrue;
            foreach (var box in Narrowing.AlwaysTrueTogether(guard, values, spaces, model))
            {
                box[0] &= matched;
                boxes.Add(box);
            }
        }

        return [.. space.Cases.Cast<object?>().Concat(space.NullMatters ? [null] : [])
            .Where(candidate => Combinations.HoldWhateverTheFlags(boxes, candidate, flags.Count))
            .Select(candidate => new[] { candidate is null ? CaseSet.Null : CaseSet.Of([candidate]) })];
    }

    /// <summary>
    /// Whether a switch handles every case of its input (or inputs) that earlier code leaves, and which combinations it misses.
    /// For a switch the analysis doesn't reason about, <see cref="OverCases"/> and <see cref="Complete"/> are both false.
    /// (A switch whose arms look at unions inside its input can be complete without being over cases.)
    /// </summary>
    /// <param name="countGuarded">Whether an arm with a <c>when</c> clause that always holds for a case counts as handling it (the compiler doesn't count it).</param>
    /// <param name="withoutDefault">Whether to leave out the switch's default:
    /// a switch expression's last arm, or a switch statement's <c>default:</c> label (to work out what a throwing default catches).</param>
    public static SwitchCoverage Of(SyntaxNode switchNode, SemanticModel model, Session session, CancellationToken ct, bool countGuarded = true, bool withoutDefault = false)
    {
        var switchOperation = model.GetOperation(switchNode, ct);
        if (switchOperation switch { ISwitchExpressionOperation expression => expression.Value, ISwitchOperation statement => statement.Value, _ => null } is not { } input)
        {
            return None;
        }

        var labels = SwitchLabel.Of(switchOperation);
        if (withoutDefault)
        {
            labels = switchOperation is ISwitchExpressionOperation ? labels.Take(labels.Count - 1).ToList() : [.. labels.Where(label => !label.IsDefault)];
        }

        // No union or closed type at the top, but the arms might look at one inside (`owner switch { { Pet: Cat } => ... }`,
        // `(a, b) switch { ({ Pet: Cat }, _) => ... }`).
        var inputs = Inputs(input, switchNode, model, session, ct);
        var overCases = inputs?.Any(i => i.Space.IsOfTypes) == true;

        if (inputs is null || (inputs.Count > 1 && !overCases))
        {
            return (inputs is null || input is ITupleOperation) && NestedCoverage.Of(input, labels, switchNode, model, session, ct) is (true, true)
                ? CompleteInside
                : None;
        }

        var spaces = inputs.Select(i => i.Space).ToList();
        var handled = Handled(labels, spaces, model.Compilation);

        List<CaseSet[]> guarded = countGuarded && inputs.Count == 1 && inputs[0].Value is { } value
            ? HandledByWhenClauses(input, value, spaces[0], labels, switchNode, model, ct)
            : [];

        var (excluded, excludedTogether) = RuledOut(inputs, switchNode, model, session, ct);

        // Null counts as a case where a case type can be null: the compiler would otherwise ask for it (CS8655), and it
        // reports that only once CS8509 is out of the way, so suppressing CS8509 would hide it.
        var cases = spaces.Select(space => space.Cases.Cast<object?>().Concat(space.NullMatters ? [null] : []).ToList()).ToList();
        if (cases.Aggregate(1L, (count, c) => count * c.Count) > Limits.Combinations)
        {
            return new SwitchCoverage(overCases, complete: false, thanksToEarlierCode: false, spaces, []);
        }

        var thanksToAnalysis = false;
        var missing = new List<object?[]>();
        
        foreach (var combination in Combinations.Of(cases))
        {
            if (handled.Any(box => Combinations.InBox(box, combination)))
            {
                continue;
            }

            if (guarded.Any(box => Combinations.InBox(box, combination))
                || excludedTogether.Any(box => Combinations.InBox(box, combination))
                || excluded.Zip(combination, (set, candidate) => set.ContainsCase(candidate)).Any(x => x)
            )
            {
                thanksToAnalysis = true;
            }
            else
            {
                missing.Add(combination);
            }
        }

        if (missing.Count > 0)
        {
            var inside = inputs.Select((each, i) => each.Value is { } value
                ? RuledOutInside(value, each.Space, missing.Select(m => m[i]).OfType<ITypeSymbol>().Distinct<ITypeSymbol>(SymbolEqualityComparer.Default), switchNode, model, session, ct)
                : CaseSet.Empty
            ).ToList();

            var stillMissing = missing.Where(combination => !inside.Zip(combination, (set, candidate) => set.ContainsCase(candidate)).Any(x => x)).ToList();
            
            thanksToAnalysis |= stillMissing.Count < missing.Count;
            missing = stillMissing;
        }

        // Arms that look inside the input (at the union a case holds, or a union member) may handle what's missing at
        // the level of its cases.
        if (missing.Count > 0 && (inputs.Count == 1 || input is ITupleOperation) && NestedCoverage.Of(input, labels, switchNode, model, session, ct) is (true, var nestedThanks))
        {
            return new SwitchCoverage(overCases, complete: true, thanksToAnalysis || nestedThanks, spaces, []);
        }

        return new SwitchCoverage(overCases, complete: missing.Count == 0, thanksToAnalysis, spaces, missing);
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

internal static partial class Narrowing
{
    /// <summary>
    /// For a reference to an immutable collection (<c>ImmutableArray</c>, <c>ImmutableList</c>, <c>FrozenSet</c>, ...)
    /// the analysis follows, any one of its elements, as a step from it.
    /// </summary>
    private static TrackedValue? ElementOf(IOperation collection, Session session) =>
        Sequences.IsImmutableCollection(collection.Type) && TrackedValue.From(collection, session) is { } followed && Sequences.ElementType(collection) is { } elementType
            ? followed.Element(elementType)
            : null;

    /// <summary>
    /// What the filters in a LINQ chain say about the elements that get through (or, with <paramref name="steps"/>, about what those read from them), following it back through methods that pass elements on as they are, projections that pick a member (<c>Select(o =&gt; o.Pet)</c>, query <c>let</c>), lazy queries and immutable collections made from them held in a local that never changes, and methods that bring two chains together (<c>Concat</c>, <c>Union</c>: what both rule out).
    /// A chain that starts from an immutable collection the analysis follows also gets what earlier code says about all its elements (<c>if (pets.Any(p =&gt; p is Bird)) return;</c>).
    /// </summary>
    private static CaseSet FactsFromFilters(IOperation source, IReadOnlyList<Step> steps, CaseSpace space, SemanticModel model, Session session, CancellationToken ct, int copies)
    {
        var excluded = CaseSet.Empty;
        var current = Sequences.Unconverted(source);

        for (var calls = 0; calls < Limits.ChainLength; calls++)
        {
            if (current is ILocalReferenceOperation local && Sequences.LazyQueryIn(local.Local, model, session, ct) is { } query)
            {
                current = Sequences.Unconverted(query);
                continue;
            }

            if (current is not IInvocationOperation { TargetMethod: var method } invocation)
            {
                break;
            }

            var first = invocation.Arguments.FirstOrDefault()?.Value is { } value ? Sequences.Unconverted(value) : null;

            if (Sequences.IsEnumerableMethod(method))
            {
                if (method.Name is "Where" or "TakeWhile" && invocation.ArgumentFor("predicate") is { } predicate)
                {
                    excluded |= FilterFacts(predicate, steps, space, model, session);
                }
                else if (method.Name == "Select" && invocation.ArgumentFor("selector") is { } selector && Sequences.SelectedFrom(selector, steps, session) is { } before)
                {
                    steps = before;
                }
                else if (method.Name == "SelectMany" && Regrouped(invocation, first) is { } grouped)
                {
                    current = grouped;
                    continue;
                }
                else if (method.Name is "Cast" or "OfType" && first is not null && SymbolEqualityComparer.Default.Equals(Sequences.ElementType(first), Sequences.ElementType(invocation))) { } // a cast to what the elements already are
                else if (method.Name is "Concat" or "Union" or "UnionBy" && first is not null && invocation.ArgumentFor("second") is { } second && SymbolEqualityComparer.Default.Equals(Sequences.ElementType(first), Sequences.ElementType(second)))
                {
                    return excluded | (FactsFromFilters(first, steps, space, model, session, ct, copies) & FactsFromFilters(second, steps, space, model, session, ct, copies));
                }
                else if (!Sequences.ElementPreserving.Contains(method.Name))
                {
                    break;
                }

                current = first;
                continue;
            }

            if (Sequences.IsImmutableMaterialization(invocation) && first is not null)
            {
                current = first;
                continue;
            }

            if (method.Name == "FindAll" && invocation.ArgumentFor("match") is { } match)
            {
                excluded |= FilterFacts(match, steps, space, model, session);
                current = method.ContainingType.SpecialType == SpecialType.System_Array
                    ? first
                    : invocation.Instance is { } list
                        ? Sequences.Unconverted(list)
                        : null;

                continue;
            }

            break;
        }

        // `if (pets.Any(p => p is Bird)) return; ... pets.Select(p => ...)`: what holds for every element there.
        if (copies < Limits.Copies && current is not null && ElementOf(current, session) is { } element
            && element.Through(steps, model.Compilation, followsMutableState: false) is { } path && IsTrackable(path, current.Syntax, model, ct)
        )
        {
            excluded |= Excluded(current.Syntax, path, space, model, ct, onEntry: false, copies + 1);
        }

        return excluded;
    }

    /// <summary>
    /// For <c>groups.SelectMany(g =&gt; g)</c>, where the groups come from <c>GroupBy</c> (by a key alone) or <c>Chunk</c>, which hands back the very elements that went in:
    /// the sequence they went in from.
    /// </summary>
    private static IOperation? Regrouped(IInvocationOperation flatten, IOperation? groups) =>
        flatten.ArgumentFor("selector") is { } selector
        && Sequences.Unconverted(selector) is IAnonymousFunctionOperation { Symbol.Parameters.Length: 1, Body.Operations.Length: 1 } identity
        && identity.Body.Operations[0] is IReturnOperation { ReturnedValue: { } flattened }
        && Sequences.Unconverted(flattened) is IParameterReferenceOperation returned
        && SymbolEqualityComparer.Default.Equals(returned.Parameter, identity.Symbol.Parameters[0])
        && groups is IInvocationOperation { TargetMethod: { Name: "GroupBy" or "Chunk" } grouping } grouped
        && Sequences.IsEnumerableMethod(grouping)
        && grouped.ArgumentFor("elementSelector") is null && grouped.ArgumentFor("resultSelector") is null
        && grouped.Arguments.FirstOrDefault()?.Value is { } source
            ? Sequences.Unconverted(source)
            : null;

    /// <summary>
    /// For a lambda's parameter, where the lambda is given each element of a LINQ chain (<c>pets.Where(p =&gt; p is not Bird).Select(p =&gt; ...)</c>, the same in query syntax, <c>list.ForEach(p =&gt; ...)</c>):
    /// what the chain's filters say about the elements that get through.
    /// </summary>
    private static CaseSet FactsFromLinqSource(IParameterSymbol parameter, Context context)
    {
        var operation = Functions.PassedLambda(parameter, context.Start, context.Model, context.CancellationToken);

        if (operation?.Parent is not IArgumentOperation { Parameter.Name: var parameterName, Parent: IInvocationOperation invocation }
            || Sequences.ElementSource(invocation, parameterName, parameter.Ordinal) is not { } source
            || !SymbolEqualityComparer.Default.Equals(Sequences.ElementType(source), parameter.Type)
        )
        {
            return CaseSet.Empty;
        }

        return FactsFromFilters(source, context.Value.Steps, context.Space, context.Model, context.Value.Session, context.CancellationToken, context.Copies);
    }

    /// <summary>
    /// What a filter's predicate (a lambda that returns a condition, or a method whose body is one) says doesn't get through.
    /// </summary>
    private static CaseSet FilterFacts(IOperation predicate, IReadOnlyList<Step> steps, CaseSpace space, SemanticModel model, Session session) =>
        Facts.OfPredicate(predicate, steps, space, model, session)?.AlwaysFalse ?? CaseSet.Empty;

    /// <summary>
    /// For <c>First()</c> on <c>SkipWhile(p)</c>:
    /// the first element that's left is one <c>p</c> was false for.
    /// </summary>
    private static CaseSet FirstSkipped(IInvocationOperation invocation, IOperation sequence, IReadOnlyList<Step> steps, CaseSpace space, SemanticModel model, Session session) =>
        invocation.TargetMethod.Name is "First" or "FirstOrDefault" && invocation.Arguments.Length == 1
        && Sequences.Unconverted(sequence) is IInvocationOperation { TargetMethod: { Name: "SkipWhile" } skipWhile } skipping && Sequences.IsEnumerableMethod(skipWhile)
        && skipping.ArgumentFor("predicate") is { } predicate
        && Facts.OfPredicate(predicate, steps, space, model, session) is { } outcome
            ? outcome.AlwaysTrue
            : CaseSet.Empty;
}

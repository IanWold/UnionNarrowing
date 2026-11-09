using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What LINQ (and the collection methods like it) does with a sequence's elements, as far as following them goes.
/// </summary>
internal static class Sequences
{
    /// <summary>
    /// LINQ methods that make a new list or array, which the code holding it could change later.
    /// </summary>
    private static readonly string[] Materializing = ["ToList", "ToArray", "ToHashSet"];

    /// <summary>
    /// LINQ methods that pass on (some of) their source's elements as they are.
    /// </summary>
    public static readonly ImmutableArray<string> ElementPreserving =
    [
        "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Order", "OrderDescending", "Reverse",
        "Take", "Skip", "TakeLast", "SkipLast", "TakeWhile", "SkipWhile", "Distinct", "DistinctBy", "AsEnumerable",
        "Except", "ExceptBy", "Intersect", "IntersectBy",
        "ToList", "ToArray", "ToHashSet", "AsParallel", "AsSequential", "AsOrdered", "AsUnordered", "WithDegreeOfParallelism",
        "WithCancellation", "WithExecutionMode", "WithMergeOptions",
    ];

    /// <summary>
    /// What's passed for the parameter with this name, if anything is.
    /// </summary>
    public static IOperation? ArgumentFor(this IInvocationOperation invocation, string parameterName) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName)?.Value;

    /// <summary>
    /// Where in a tuple literal the element for <paramref name="field"/> (by name, or as <c>Item1</c>, ...)
    /// is, or -1.
    /// </summary>
    public static int ElementIndex(ITupleOperation tuple, IFieldSymbol field)
    {
        if (tuple.Type is not INamedTypeSymbol tupleType)
        {
            return -1;
        }

        var index = tupleType.TupleElements.Select(e => e.CorrespondingTupleField ?? e).ToList().FindIndex(e => SymbolEqualityComparer.Default.Equals(e, field.CorrespondingTupleField ?? field));
        
        return index < tuple.Elements.Length ? index : -1;
    }

    /// <summary>
    /// For a call that gives back one element of a sequence (<c>First</c>, <c>Single(predicate)</c>, <c>MaxBy</c>, <c>list.Find(match)</c>, ...):
    /// the sequence, the predicate the element passed (if any), and whether it can be the default instead (which holds no case, so null isn't ruled out).
    /// </summary>
    public static (IOperation Sequence, IOperation? Predicate, bool MayBeDefault)? ElementResult(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (invocation.ArgumentFor("defaultValue") is not null)
        {
            return null;
        }

        if (IsEnumerableMethod(method) && invocation.Arguments.FirstOrDefault()?.Value is { } source)
        {
            return method.Name switch
            {
                "First" or "Last" or "Single" or "ElementAt" => (source, invocation.ArgumentFor("predicate"), false),
                "FirstOrDefault" or "LastOrDefault" or "SingleOrDefault" or "ElementAtOrDefault" => (source, invocation.ArgumentFor("predicate"), true),
                "MaxBy" or "MinBy" => (source, null, method.ReturnType.IsReferenceType), // empty: throws for a struct, null for a class
                _ => ((IOperation, IOperation?, bool)?)null,
            };
        }

        if (method.Name is "Find" or "FindLast" && invocation.ArgumentFor("match") is { } match)
        {
            if (Symbols.IsList(method.ContainingType) && invocation.Instance is { } list)
            {
                return (list, match, true);
            }

            if (method.ContainingType.SpecialType == SpecialType.System_Array && invocation.Arguments.FirstOrDefault()?.Value is { } array)
            {
                return (array, match, true);
            }
        }

        return null;
    }

    /// <summary>
    /// For a lambda passed to <paramref name="invocation"/> as <paramref name="parameterName"/>, the sequence whose elements its parameter at <paramref name="ordinal"/> gets, if it gets them as they are.
    /// </summary>
    public static IOperation? ElementSource(IInvocationOperation invocation, string parameterName, int ordinal)
    {
        var method = invocation.TargetMethod;
        var first = invocation.Arguments.FirstOrDefault()?.Value;

        if (IsEnumerableMethod(method))
        {
            return (parameterName, ordinal, method.Name) switch
            {
                ("predicate" or "selector" or "keySelector" or "elementSelector" or "collectionSelector" or "outerKeySelector", 0, _) => first,
                ("innerKeySelector", 0, _) => invocation.ArgumentFor("inner"),
                ("resultSelector", 0, "Zip" or "SelectMany" or "Join" or "GroupJoin") => first,
                ("resultSelector", 1, "Zip") => invocation.ArgumentFor("second"),
                ("resultSelector", 1, "Join") => invocation.ArgumentFor("inner"),
                ("resultSelector", 1, "SelectMany") => CollectionOf(invocation.ArgumentFor("collectionSelector")),
                ("func", 1, "Aggregate") when invocation.ArgumentFor("seed") is not null => first,
                _ => null,
            };
        }

        // List<T> and Array methods that hand each element to a delegate.
        if (parameterName is "action" or "match" or "converter" && ordinal == 0)
        {
            if (Symbols.IsList(method.ContainingType))
            {
                return invocation.Instance;
            }

            if (method.ContainingType.SpecialType == SpecialType.System_Array)
            {
                return first;
            }
        }

        return null;

        // For SelectMany's result selector: the sequence the collection selector gives, if it's one whose elements are
        // known: one that doesn't depend on the element (`p => others`), or a group from GroupBy (`g => g`), whose
        // elements are GroupBy's source's.
        IOperation? CollectionOf(IOperation? selector)
        {
            if (selector is null || Unconverted(selector) is not IAnonymousFunctionOperation { Body.Operations.Length: 1 } lambda || lambda.Body.Operations[0] is not IReturnOperation { ReturnedValue: { } collection })
            {
                return null;
            }

            var element = lambda.Symbol.Parameters[0];
            var returned = Unconverted(collection);

            if (returned is IParameterReferenceOperation { Parameter: var p } && SymbolEqualityComparer.Default.Equals(p, element))
            {
                return first is not null && Unconverted(first) is IInvocationOperation { TargetMethod: { Name: "GroupBy" } groupBy } grouping
                    && IsEnumerableMethod(groupBy) && grouping.Arguments.Length == 2
                    ? grouping.Arguments[0].Value
                    : null;
            }

            return returned.DescendantsAndSelf().OfType<IParameterReferenceOperation>().Any(r => SymbolEqualityComparer.Default.Equals(r.Parameter, element))
                ? null
                : returned;
        }
    }

    /// <summary>
    /// The element type of a sequence (its IEnumerable&lt;T&gt;'s T).
    /// </summary>
    public static ITypeSymbol? ElementType(IOperation sequence) =>
        Unconverted(sequence).Type is { } type
            ? (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } named ? named : type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T))?.TypeArguments.FirstOrDefault()
            : null;

    /// <summary>
    /// Methods of LINQ (to objects, parallel, or over an ImmutableArray), which behave as documented.
    /// </summary>
    public static bool IsEnumerableMethod(IMethodSymbol method) =>
        method.ContainingType is { Name: "Enumerable" or "ParallelEnumerable" or "ImmutableArrayExtensions" } type && Symbols.Is(type, type.Name, 0, "System", "Linq");

    /// <summary>
    /// Collections whose elements never change once made.
    /// </summary>
    public static bool IsImmutableCollection(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Arity: 1, Name: var name }
        && (name is "ImmutableArray" or "ImmutableList" or "ImmutableHashSet" or "ImmutableSortedSet" or "ImmutableQueue" or "ImmutableStack"
            ? Symbols.Is(type, name, 1, "System", "Collections", "Immutable")
            : Symbols.Is(type, "FrozenSet", 1, "System", "Collections", "Frozen")
        );

    /// <summary>
    /// A call that makes an immutable collection of a sequence's elements (<c>ToImmutableArray()</c>, <c>ToFrozenSet()</c>, ...), which no one can change later, unlike a list or array.
    /// </summary>
    public static bool IsImmutableMaterialization(IInvocationOperation invocation) =>
        invocation.TargetMethod is { Name: "ToImmutableArray" or "ToImmutableList" or "ToImmutableHashSet" or "ToImmutableSortedSet" or "ToFrozenSet", IsExtensionMethod: true }
        && IsImmutableCollection(invocation.Type)
        && invocation.Arguments.FirstOrDefault()?.Value is { } source
        && SymbolEqualityComparer.Default.Equals(ElementType(Unconverted(source)), ElementType(invocation));

    /// <summary>
    /// For a local that never changes, set to a lazy LINQ query (not to a list or array, which could be changed later), that query.
    /// </summary>
    public static IOperation? LazyQueryIn(ILocalSymbol local, SemanticModel model, Session session, CancellationToken ct) =>
        local.DeclaringSyntaxReferences is not { Length: 1 } references
        || references[0].GetSyntax(ct) is not VariableDeclaratorSyntax { Initializer.Value: { } initializer } declarator
        || declarator.SyntaxTree != model.SyntaxTree
        || session.UsageOf(local, declarator, model, ct) is not { NeverChanges: true }
        || model.GetOperation(declarator, ct) is not IVariableDeclaratorOperation { Initializer.Value: { } query }
        || Unconverted(query) is not IInvocationOperation { TargetMethod: var method } call
        || !(IsEnumerableMethod(method) && !Materializing.Contains(method.Name) || IsImmutableMaterialization(call))
            ? null
            : query;

    /// <summary>
    /// For a Select whose selector picks a member of its element (or the element itself), the steps from the source's elements that <paramref name="steps"/> from the results are:
    /// <c>.Pet</c> for <c>Select(o =&gt; o.Pet)</c>, or <c>.p</c> for the <c>q</c> of <c>let q = p</c>.
    /// Not through what only something else could change (see <see cref="TrackedValue.IsExternallyMutable"/>), which could change between the filter and the selector.
    /// </summary>
    public static IReadOnlyList<Step>? SelectedFrom(IOperation selector, IReadOnlyList<Step> steps, Session session)
    {
        if (Unconverted(selector) is not IAnonymousFunctionOperation { Symbol.Parameters.Length: > 0, Body.Operations.Length: 1 } lambda || lambda.Body.Operations[0] is not IReturnOperation { ReturnedValue: { } returned })
        {
            return null;
        }

        var element = lambda.Symbol.Parameters[0];
        var result = Unconverted(returned);
        var rest = steps;

        // `new { p, q = p }.q`, `(p, 1).Item1`: the part the member is made from.
        if (steps.Count > 0 && steps[0] is MemberStep { Member: var member })
        {
            IOperation? part = result switch
            {
                IAnonymousObjectCreationOperation anonymous => anonymous.Initializers.OfType<ISimpleAssignmentOperation>()
                    .FirstOrDefault(a => a.Target is IPropertyReferenceOperation target && SymbolEqualityComparer.Default.Equals(target.Property, member))?.Value,
                ITupleOperation tuple when member is IFieldSymbol field && ElementIndex(tuple, field) is var index and >= 0 => tuple.Elements[index],
                _ => null,
            };

            if (part is not null)
            {
                result = Unconverted(part);
                rest = [.. steps.Skip(1)];
            }
        }

        return TrackedValue.From(result, session) is { IsExternallyMutable: false } picked && SymbolEqualityComparer.Default.Equals(picked.Variable, element)
            ? picked.Steps.Concat(rest).ToList()
            : null;
    }

    public static IOperation Unconverted(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } or IDelegateCreationOperation)
        {
            operation = operation is IConversionOperation conversion ? conversion.Operand : ((IDelegateCreationOperation)operation).Target;
        }

        return operation;
    }
}

using System.Collections.Generic;
using System.Linq;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Combinations of cases, one per input, and boxes of them:
/// a box has a set of cases per input, and stands for every combination whose cases are each in their input's set.
/// </summary>
internal static class Combinations
{
    /// <summary>
    /// For boxes over a value and some bools (the value's cases first, then each bool's):
    /// whether they hold <paramref name="candidate"/>, one of the value's cases, whatever the bools are.
    /// <c>(Bird, true)</c> and <c>(Bird, false)</c> between them hold Bird.
    /// </summary>
    public static bool HoldWhateverTheFlags(IReadOnlyList<CaseSet[]> boxes, object? candidate, int flags) =>
        Of([.. Enumerable.Repeat<IReadOnlyList<object?>>([true, false], flags)]).All(values => boxes.Any(box => InBox(box, [candidate, .. values])));

    public static bool InBox(CaseSet[] box, object?[] combination) =>
        box.Zip(combination, (set, candidate) => set.ContainsCase(candidate)).All(x => x);

    /// <summary>
    /// Every combination of one choice per input.
    /// </summary>
    public static IEnumerable<object?[]> Of(IReadOnlyList<IReadOnlyList<object?>> choices)
    {
        IEnumerable<object?[]> combinations = [[]];
        foreach (var options in choices)
        {
            combinations = combinations.SelectMany(prefix => options.Select(option => prefix.Append(option).ToArray()));
        }

        return combinations;
    }
}

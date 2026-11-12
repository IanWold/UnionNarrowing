namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// How far the analysis goes.
/// Past any of these it stops and knows nothing more there, which only means it suppresses (and reports) less.
/// The README states the ones that show.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// The most boxes a combination of guards on several values is worked out to.
    /// </summary>
    public const int Boxes = 64;

    /// <summary>
    /// How many calls back a LINQ chain is followed.
    /// </summary>
    public const int ChainLength = 32;

    /// <summary>
    /// The most combinations of cases a switch is checked for, over all its inputs (and the places nested patterns look at).
    /// </summary>
    public const int Combinations = 4096;

    /// <summary>
    /// How many members of a type its constructors and initializers are followed into, to find what can run while an object is constructed.
    /// </summary>
    public const int ConstructionCalls = 32;

    /// <summary>
    /// How many copies back (<c>var b = a; var c = b;</c>) a value is followed, or more generally, how many times one walk starts another (from a copy, a call, an assignment).
    /// </summary>
    public const int Copies = 6;

    /// <summary>
    /// How many bools a case can be split over:
    /// <c>pet is Bird &amp;&amp; flag</c> with <c>pet is Bird &amp;&amp; !flag</c>, or <c>when</c> clauses that test them.
    /// </summary>
    public const int Flags = 2;

    /// <summary>
    /// How many getters deep (a getter returning a property whose getter returns ...)
    /// a path is followed.
    /// </summary>
    public const int GetterDepth = 4;

    /// <summary>
    /// How many helpers deep (a helper calling a helper) a condition is read through.
    /// </summary>
    public const int HelperDepth = 3;

    /// <summary>
    /// How many conditionals and coalesces deep a switch's input is followed:
    /// <c>(flag ? a : b) switch</c>.
    /// </summary>
    public const int InputDepth = 4;

    /// <summary>
    /// How many members deep a nested pattern is followed (<c>A { B: C { D: E } }</c> is two).
    /// </summary>
    public const int NestedDepth = 4;

    /// <summary>
    /// How many places nested patterns can look at (see <see cref="NestedCoverage"/>).
    /// </summary>
    public const int NestedPlaces = 6;

    /// <summary>
    /// How many structs deep a union inside a struct is looked for (UN0018).
    /// </summary>
    public const int StructNesting = 4;

    /// <summary>
    /// The most missing combinations written out as nested patterns.
    /// </summary>
    public const int WrittenOutMissing = 16;
}

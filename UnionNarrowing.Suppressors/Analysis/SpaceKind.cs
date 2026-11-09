namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// What kind of value a <see cref="CaseSpace"/> is about.
/// </summary>
internal enum SpaceKind
{
    /// <summary>
    /// A union: its cases are its case types, which patterns test its <c>Value</c> against.
    /// </summary>
    Union,

    /// <summary>
    /// A closed hierarchy:
    /// its cases are its leaf types.
    /// </summary>
    Closed,

    /// <summary>
    /// A bool: its cases are <c>true</c> and <c>false</c>.
    /// </summary>
    Bool,

    /// <summary>
    /// An enum: its cases are its members' distinct values, plus <see cref="CaseSpace.OtherValues"/> for every other value (unless CS8524, which is about those, is turned off).
    /// </summary>
    Enum,

    /// <summary>
    /// Anything else, in a tuple with a union:
    /// a single case, <see cref="CaseSpace.Anything"/>, that only a pattern that matches every non-null value handles.
    /// </summary>
    Opaque,
}

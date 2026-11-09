using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analyzers;

internal static class Descriptors
{
    private const string Reference = "https://github.com/IanWold/UnionNarrowing/wiki/Analyzers#";

    public static readonly DiagnosticDescriptor BoxedUnionTest = new(
        id: "UN0014",
        title: "Boxed union never matches a case",
        helpLinkUri: Reference + "un0014-boxed-union-never-matches-a-case",
        messageFormat: "'{0}' {1}",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A union is a struct, and boxing it (to object, or a generic T) boxes the union itself, not what it holds. A boxed Pet is a Pet "
            + "at run time, so a type test against one of its cases is never true, a cast to one throws, and Equals with one of its cases is false. "
            + "Test the union itself, which tests what it holds, or its Value."
    );

    public static readonly DiagnosticDescriptor DeadArm = new(
        id: "UN0004",
        title: "Switch arm, case or test can't match",
        helpLinkUri: Reference + "un0004-switch-arm-case-or-test-cant-match",
        messageFormat: "This {0} can't match: earlier code already ruled out {1}",
        category: "Style",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Every case this switch arm, case label or type test could match was ruled out before it, so it never matches. "
            + "Removing an arm or label doesn't make the switch any less exhaustive.",
        customTags: WellKnownDiagnosticTags.Unnecessary
    );

    public static readonly DiagnosticDescriptor DefaultStructWithUnion = new(
        id: "UN0018",
        title: "Default struct holds a union with no case",
        helpLinkUri: Reference + "un0018-default-struct-holds-a-union-with-no-case",
        messageFormat: "'{0}' leaves default({1}) in {2}, which holds none of its cases",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The default value of a struct (or tuple) is all zeros, so a struct union inside it is a default union, whose Value is null and "
            + "matches none of the case types. A switch that handles every case type throws on it. Construct the struct with a value for the union instead."
    );

    public static readonly DiagnosticDescriptor DefaultUnion = new(
        id: "UN0003",
        title: "Default union holds no case",
        helpLinkUri: Reference + "un0003-default-union-holds-no-case",
        messageFormat: "{0} holds none of {1}'s cases, so a switch over it throws",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The default value of a struct union has a null Value, which matches none of its case types. The compiler treats a switch "
            + "that handles every case type as exhaustive, so it doesn't warn, but the switch throws SwitchExpressionException on a default union. "
            + "Construct the union from one of its cases instead."
    );

    public static readonly DiagnosticDescriptor DefaultUnionArray = new(
        id: "UN0006",
        title: "Array of unions holds elements with no case",
        helpLinkUri: Reference + "un0006-array-of-unions-holds-elements-with-no-case",
        messageFormat: "'{0}' {2} default({1}), which holds none of its cases",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A new (or grown, cleared or pooled) array of a struct union holds default unions, whose Value is null and matches none of "
            + "the case types. A switch that handles every case type throws on one. Fill every element before reading it, or create the array from its elements."
    );

    public static readonly DiagnosticDescriptor DefaultUnionFallback = new(
        id: "UN0010",
        title: "Union can come back as default",
        helpLinkUri: Reference + "un0010-union-can-come-back-as-default",
        messageFormat: "'{0}' gives default({1}) {2}, which holds none of its cases",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Methods like FirstOrDefault and GetValueOrDefault fall back to the default value, and a struct union's default has a null Value, "
            + "which matches none of the case types: a switch that handles every case type throws on it. Use the overload that takes a fallback value, "
            + "or a Try method."
    );

    public static readonly DiagnosticDescriptor RecordPrintsUnion = new(
        id: "UN0019",
        title: "Record's ToString shows a union member's type name",
        helpLinkUri: Reference + "un0019-records-tostring-shows-a-union-members-type-name",
        messageFormat: "'{0}' prints its {1} as '{2}', the union's type name, in ToString: declare PrintMembers to print its Value",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A record's generated ToString prints each public member with its own ToString, and a union doesn't override ToString, so "
            + "it prints the union's type name rather than what the member holds. Declare PrintMembers (or ToString) to print its Value."
    );

    public static readonly DiagnosticDescriptor SwitchOnValue = new(
        id: "UN0005",
        title: "Switch on or test the union, not its Value",
        helpLinkUri: Reference + "un0005-switch-on-or-test-the-union-not-its-value",
        messageFormat: "{1} '{0}' instead of '{0}.Value', so the compiler checks {2}",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Patterns on a union test its Value, so switching on (or testing) the union matches the same way, and the compiler can "
            + "check the patterns against the union's case types: that a switch handles every case, and that a test is for a case the union can hold. "
            + "Value is just an object."
    );

    public static readonly DiagnosticDescriptor ThrowingDefault = new(
        id: "UN0002",
        title: "Cases fall through to a throwing default",
        helpLinkUri: Reference + "un0002-cases-fall-through-to-a-throwing-default",
        messageFormat: "The throwing {0} catches {1}, which the {2} doesn't otherwise handle",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A switch over a union or closed hierarchy that ends in a default that only throws is meant to handle every case. "
            + "Cases that reach the default instead fail at run time, and so will any case added to the type later, with no warning. "
            + "Handle them explicitly, and the compiler (or this analyzer) will point out the next case that isn't. The same goes for a chain of "
            + "if/else if tests that ends in an else that only throws."
    );

    public static readonly DiagnosticDescriptor ThrowingDefaultOnCompleteSwitch = new(
        id: "UN0016",
        title: "Throwing default only catches cases added later",
        helpLinkUri: Reference + "un0016-throwing-default-only-catches-cases-added-later",
        messageFormat: "This throwing {0} only catches cases added to {1} later, which the compiler would otherwise report as missing",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: false,
        description: "A switch expression over a union or closed hierarchy that already handles every case doesn't need a throwing default arm. "
            + "All the arm does is catch any case added to the type later, at run time, instead of the compiler reporting the switch as not "
            + "exhaustive. Without it, a default union throws SwitchExpressionException instead of the arm's exception."
    );

    public static readonly DiagnosticDescriptor UnassignedUnionMember = new(
        id: "UN0007",
        title: "Union member can be left holding no case",
        helpLinkUri: Reference + "un0007-union-member-can-be-left-holding-no-case",
        messageFormat: "'{0}' isn't assigned by every constructor, so it can be left as default({1}), which holds none of its cases",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A readonly field or get-only auto-property can only be assigned while the object is constructed. One a constructor doesn't "
            + "assign keeps the union's default value, whose Value is null and matches none of the case types, and a switch over it throws. "
            + "Give it an initializer, or assign it in every constructor."
    );

    public static readonly DiagnosticDescriptor UnionAsText = new(
        id: "UN0015",
        title: "Union formatted as text shows its type name",
        helpLinkUri: Reference + "un0015-union-formatted-as-text-shows-its-type-name",
        messageFormat: "'{0}' shows the union's type name, '{1}', not what it holds: format {2} instead",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A union doesn't override ToString, so formatting one (with ToString, string interpolation, concatenation, Console.WriteLine, "
            + "StringBuilder.Append, string.Join or a logger) gives its type name rather than the value it holds. Format its Value instead."
    );
}

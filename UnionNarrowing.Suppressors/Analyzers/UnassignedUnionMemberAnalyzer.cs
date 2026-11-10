using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports a field or auto-property of a struct union type (or of a struct with one inside), in a class, that some constructor can leave unassigned (UN0007):
/// unless it has an initializer, it starts out as the union's default, which holds none of its cases, so a switch the compiler considers exhaustive throws on it.
/// Much as CS8618 is for a reference that isn't nullable, and with the same ways out:
/// a <c>required</c> member is left to whoever creates the object (unless a <c>[SetsRequiredMembers]</c> constructor says it sets it), and a <c>[MemberNotNull]</c> method a constructor calls assigns what it names.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnassignedUnionMemberAnalyzer : DiagnosticAnalyzer
{
    private static ImmutableHashSet<ISymbol> None =>
        ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.UnassignedUnionMember];

    /// <summary>
    /// For a class, works out what each constructor assigns, then reports any member a constructor that has to assign it can leave out.
    /// </summary>
    private static void AnalyzeType(SymbolStartAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
        {
            return;
        }

        var candidates = type.GetMembers().Select(MustBeAssignedByConstructors).OfType<ISymbol>().ToImmutableHashSet(SymbolEqualityComparer.Default);
        if (candidates.IsEmpty)
        {
            return;
        }

        var assignments = new ConcurrentDictionary<IMethodSymbol, (ImmutableHashSet<ISymbol> Assigned, IMethodSymbol? Chained)>(SymbolEqualityComparer.Default);

        context.RegisterOperationBlockAction(block =>
        {
            if (block.OwningSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor
                && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, type)
                && block.OperationBlocks.FirstOrDefault(b => b.Parent is IConstructorBodyOperation) is { } body
            )
            {
                assignments[constructor] = Assignments(block.GetControlFlowGraph(body), type);
            }
        });

        context.RegisterSymbolEndAction(end =>
        {
            foreach (var member in candidates)
            {
                var (constructors, by) = member switch
                {
                    { IsStatic: true } => (type.StaticConstructors, "an initializer or the static constructor"),
                    IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true } => ([.. type.InstanceConstructors.Where(SetsRequiredMembers)], "every constructor with [SetsRequiredMembers]"),
                    _ => ([.. type.InstanceConstructors.Where(c => !IsRecordCopyConstructor(c))], "every constructor"),
                };

                // Nothing assigns a static member if there's no static constructor; nothing has to assign a required one if
                // no constructor says it does.
                var leftOut = (member.IsStatic && constructors.IsEmpty)
                    || constructors.Any(c => !AssignedBy(c, assignments).Contains(member));
                if (!leftOut)
                {
                    continue;
                }

                var memberType = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
                var leftAs = DefaultUnionAnalyzer.UnionInside(memberType) is var (union, path)
                    ? $"default({Display(memberType)}), with default({Display(union)}) in {path}"
                    : $"default({Display(memberType)})";

                end.ReportDiagnostic(Diagnostic.Create(Descriptors.UnassignedUnionMember, member.Locations[0], member.Name, by, leftAs));
            }
        });

        static string Display(ITypeSymbol type) =>
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    /// <summary>
    /// Whether the constructor is a record's copy constructor that the compiler generates for <c>with</c>:
    /// it copies every field from an object that was already constructed.
    /// </summary>
    private static bool IsRecordCopyConstructor(IMethodSymbol constructor) =>
        constructor is { IsImplicitlyDeclared: true, ContainingType.IsRecord: true, Parameters.Length: 1 }
        && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, constructor.ContainingType);

    /// <summary>
    /// The names a <c>[MemberNotNull]</c> method gives for the members it assigns.
    /// </summary>
    private static IEnumerable<string> MembersNotNull(IMethodSymbol method) =>
        method.GetAttributes()
            .Where(attribute => Symbols.IsAttribute(attribute, "MemberNotNullAttribute", "System", "Diagnostics", "CodeAnalysis"))
            .SelectMany(attribute => attribute.ConstructorArguments)
            .SelectMany(argument => argument.Kind == TypedConstantKind.Array ? argument.Values : [argument])
            .Select(argument => argument.Value)
            .OfType<string>();

    /// <summary>
    /// For a field of a struct union type whose default holds no case (or of a struct with one inside), with no initializer, the member constructors have to assign so it doesn't start out holding no case:
    /// the field itself, or the property it's the backing field of (an auto-property, or one that uses <c>field</c>).
    /// Like a non-nullable reference the constructor leaves null, which CS8618 is about.
    /// </summary>
    private static ISymbol? MustBeAssignedByConstructors(ISymbol member)
    {
        if (member is not IFieldSymbol { IsConst: false } field
            || !(CaseSpace.DefaultHoldsNoCase(field.Type) || DefaultUnionAnalyzer.UnionInside(field.Type) is not null)
            || (field.IsImplicitlyDeclared ? field.AssociatedSymbol as IPropertySymbol : (ISymbol)field) is not { } named
        )
        {
            return null;
        }

        var declarations = named is IPropertySymbol { PartialImplementationPart: { } implementation } ? implementation.DeclaringSyntaxReferences : named.DeclaringSyntaxReferences;
        var hasInitializer = !declarations.All(reference => reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: null } or PropertyDeclarationSyntax { Initializer: null });

        return hasInitializer ? null : Named(named);
    }

    /// <summary>
    /// A member as assignments to it name it:
    /// a partial property by its defining declaration.
    /// </summary>
    private static ISymbol Named(ISymbol member) =>
        member.OriginalDefinition is IPropertySymbol { PartialDefinitionPart: { } definition } ? definition : member.OriginalDefinition;

    private static bool SetsRequiredMembers(IMethodSymbol constructor) =>
        Symbols.HasAttribute(constructor, "SetsRequiredMembersAttribute", "System", "Diagnostics", "CodeAnalysis");

    /// <summary>
    /// The members of <paramref name="type"/>, on this object (or static), that an operation assigns:
    /// the target of an assignment (or of a deconstruction), an <c>out</c> or <c>ref</c> argument, or what a <c>[MemberNotNull]</c> method it calls on this object names.
    /// </summary>
    /// <param name="resolved">What a flow capture in the operation stands for.</param>
    private static IEnumerable<ISymbol> Written(IOperation operation, INamedTypeSymbol type, Func<IOperation, IOperation> resolved)
    {
        static bool OnThis(IOperation? instance) =>
            instance is null or IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance };

        IEnumerable<IOperation> Targets(IOperation target) =>
            resolved(target) is ITupleOperation tuple ? tuple.Elements.SelectMany(Targets) : [resolved(target)];

        if (operation is IInvocationOperation call && OnThis(call.Instance))
        {
            return MembersNotNull(call.TargetMethod).SelectMany(name => type.GetMembers(name)).Select(Named);
        }

        var targets = operation switch
        {
            IAssignmentOperation assignment => Targets(assignment.Target),
            IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } argument => Targets(argument.Value),
            _ => [],
        };

        return targets
            .OfType<IMemberReferenceOperation>()
            .Where(reference => reference.Member is IFieldSymbol or IPropertySymbol
                && OnThis(reference.Instance)
                && SymbolEqualityComparer.Default.Equals(reference.Member.ContainingType.OriginalDefinition, type.OriginalDefinition)
            )
            .Select(reference => Named(reference.Member));
    }

    /// <summary>
    /// What a constructor assigns, along with what the constructors it chains to with <c>: this(...)</c> assign, given what each assigns itself.
    /// </summary>
    internal static ImmutableHashSet<ISymbol> AssignedBy(IMethodSymbol constructor, IReadOnlyDictionary<IMethodSymbol, (ImmutableHashSet<ISymbol> Assigned, IMethodSymbol? Chained)> assignments)
    {
        var assigned = None;
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        for (IMethodSymbol? current = constructor; current is not null && seen.Add(current) && assignments.TryGetValue(current, out var own); current = own.Chained)
        {
            assigned = assigned.Union(own.Assigned);
        }

        return assigned;
    }

    /// <summary>
    /// The members of <paramref name="type"/> a constructor assigns on every way through it that gets to its end (a way that throws doesn't), from its control flow graph;
    /// and the constructor of the type it chains to with <c>: this(...)</c>, which assigns the rest.
    /// </summary>
    internal static (ImmutableHashSet<ISymbol> Assigned, IMethodSymbol? Chained) Assignments(ControlFlowGraph graph, INamedTypeSymbol type)
    {
        var operations = graph.Blocks
            .Select(block => block.Operations.Append(block.BranchValue).OfType<IOperation>().SelectMany(operation => operation.DescendantsAndSelf()).ToList())
            .ToList();

        var captured = new Dictionary<CaptureId, IOperation>();
        foreach (var capture in operations.SelectMany(o => o).OfType<IFlowCaptureOperation>())
        {
            captured[capture.Id] = capture.Value;
        }

        IOperation Resolved(IOperation operation) =>
            operation is IFlowCaptureReferenceOperation reference && captured.TryGetValue(reference.Id, out var value) ? value : operation;

        var written = operations.Select(block => block.SelectMany(operation => Written(operation, type, Resolved)).ToImmutableHashSet(SymbolEqualityComparer.Default)).ToList();
        var after = new ImmutableHashSet<ISymbol>?[graph.Blocks.Length];

        // What's assigned before a block: what every way into it has assigned (null until some way in is worked out).
        // Where a catch or finally starts, nothing is known.
        ImmutableHashSet<ISymbol>? Before(BasicBlock block) =>
            !block.IsReachable
            ? null
            : block.Predecessors.IsEmpty
                ? None
                : block.Predecessors.Select(p => after[p.Source.Ordinal]).OfType<ImmutableHashSet<ISymbol>>().Aggregate((ImmutableHashSet<ISymbol>?)null, (all, each) => all?.Intersect(each) ?? each);

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var block in graph.Blocks)
            {
                if (Before(block)?.Union(written[block.Ordinal]) is { } assigned && after[block.Ordinal]?.SetEquals(assigned) != true)
                {
                    after[block.Ordinal] = assigned;
                    changed = true;
                }
            }
        }

        var chained = operations.SelectMany(o => o).OfType<IInvocationOperation>().FirstOrDefault(call =>
            call is { TargetMethod.MethodKind: MethodKind.Constructor, Instance: IInstanceReferenceOperation }
            && SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, type)
        );

        // A constructor that never gets to its end leaves nothing out.
        var atEnd = Before(graph.Blocks[graph.Blocks.Length - 1])
            ?? type.GetMembers().Where(m => m is IFieldSymbol or IPropertySymbol).Select(Named).ToImmutableHashSet(SymbolEqualityComparer.Default);

        return (atEnd, chained?.TargetMethod);
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolStartAction(AnalyzeType, SymbolKind.NamedType);
    }
}

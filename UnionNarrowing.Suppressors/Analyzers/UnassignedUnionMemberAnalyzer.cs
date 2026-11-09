using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using UnionNarrowing.Suppressors.Analysis;

namespace UnionNarrowing.Suppressors.Analyzers;

/// <summary>
/// Reports a field or auto-property of a struct union type, in a class, that some constructor doesn't assign (UN0007):
/// unless it's <c>required</c> or has an initializer, it starts out as the union's default, which holds none of its cases, so a switch the compiler considers exhaustive throws on it.
/// Much as CS8618 is for a reference that isn't nullable.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnassignedUnionMemberAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Descriptors.UnassignedUnionMember];

    /// <summary>
    /// For a class, collects which union members each constructor assigns, then reports any member some constructor leaves out.
    /// </summary>
    private static void AnalyzeType(SymbolStartAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
        {
            return;
        }

        var candidates = type.GetMembers().Where(MustBeAssignedByConstructors).ToImmutableHashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (candidates.IsEmpty)
        {
            return;
        }

        var assignedBy = new ConcurrentDictionary<IMethodSymbol, ImmutableHashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var chaining = new ConcurrentDictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);

        context.RegisterOperationBlockAction(block =>
        {
            if (block.OwningSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor || !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, type))
            {
                return;
            }

            var operations = block.OperationBlocks.SelectMany(b => b.DescendantsAndSelf()).ToList();
            assignedBy[constructor] = operations.Select(AssignedMember).Where(m => m is not null && candidates.Contains(m)).ToImmutableHashSet(SymbolEqualityComparer.Default)!;

            // `: this(...)` leaves the assigning to the constructor it calls.
            chaining[constructor] = operations.OfType<IInvocationOperation>().Any(invocation =>
                invocation is { TargetMethod.MethodKind: MethodKind.Constructor, Instance: IInstanceReferenceOperation }
                && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, type)
            );
        });

        context.RegisterSymbolEndAction(end =>
        {
            foreach (var member in candidates)
            {
                var constructors = member.IsStatic
                    ? type.StaticConstructors
                    : type.InstanceConstructors;

                // No static constructor means nothing assigns a static member; an implicit or primary constructor
                // (no body of its own) assigns nothing either.
                var leftOut = constructors.IsEmpty
                    || constructors.Any(c => !(chaining.TryGetValue(c, out var chains) && chains)
                        && !(assignedBy.TryGetValue(c, out var assigned) && assigned.Contains(member))
                    );
                if (!leftOut)
                {
                    continue;
                }

                var unionType = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
                var name = unionType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                
                end.ReportDiagnostic(Diagnostic.Create(Descriptors.UnassignedUnionMember, member.Locations[0], member.Name, name));
            }
        });
    }

    /// <summary>
    /// The field or property an operation writes, if it's a write to one.
    /// </summary>
    private static ISymbol? AssignedMember(IOperation operation)
    {
        if (operation is not (IFieldReferenceOperation or IPropertyReferenceOperation))
        {
            return null;
        }

        var target = operation;
        while (target.Parent is ITupleOperation tuple) // (a, b) = ...
        {
            target = tuple;
        }

        var written = target.Parent switch
        {
            IAssignmentOperation assignment => assignment.Target == target,
            IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } => true,
            _ => false,
        };

        return !written ? null : operation switch
        {
            IFieldReferenceOperation field => field.Field.OriginalDefinition,
            IPropertyReferenceOperation property => property.Property.OriginalDefinition,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the member is a field or auto-property of a union type whose default holds no case, with no initializer, that isn't <c>required</c>:
    /// so unless a constructor assigns it, it starts out holding no case (like a non-nullable reference the constructor leaves null, which CS8618 is about).
    /// </summary>
    private static bool MustBeAssignedByConstructors(ISymbol member) =>
        member switch
        {
            IFieldSymbol { IsImplicitlyDeclared: false, IsConst: false, IsRequired: false } field =>
                CaseSpace.DefaultHoldsNoCase(field.Type)
                && field.DeclaringSyntaxReferences.All(r => r.GetSyntax() is VariableDeclaratorSyntax { Initializer: null }),
            IPropertySymbol { IsAbstract: false, IsImplicitlyDeclared: false, IsIndexer: false, IsRequired: false } property =>
                CaseSpace.DefaultHoldsNoCase(property.Type)
                && property.DeclaringSyntaxReferences.All(r => r.GetSyntax() is PropertyDeclarationSyntax { Initializer: null } declaration && Symbols.IsAutoPropertyDeclaration(declaration)),
            _ => false,
        };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolStartAction(AnalyzeType, SymbolKind.NamedType);
    }
}

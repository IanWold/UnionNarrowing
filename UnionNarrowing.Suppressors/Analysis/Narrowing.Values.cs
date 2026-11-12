using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

internal static partial class Narrowing
{
    /// <summary>
    /// For a readonly instance field or get-only auto-property of a class, with no initializer, what each constructor sets it to:
    /// every constructor sets it exactly once, in a statement of its own body (<c>_pet = pet;</c>), or leaves that to the one it calls with <c>: this(...)</c>, and nothing else in the type assigns it or takes a reference to it.
    /// (A record's generated copy constructor copies it from an object that was made the same way.)
    /// Null otherwise.
    /// </summary>
    private static List<(IOperation Value, SemanticModel Model)>? ConstructorAssignments(ISymbol member, Compilation compilation, CancellationToken ct)
    {
        var declared = member switch
        {
            IFieldSymbol { IsReadOnly: true, IsConst: false, IsStatic: false, DeclaringSyntaxReferences.Length: 1 } field =>
                field.DeclaringSyntaxReferences[0].GetSyntax(ct) is VariableDeclaratorSyntax { Initializer: null },

            IPropertySymbol { SetMethod: null, IsStatic: false, IsAbstract: false, IsVirtual: false, IsOverride: false, IsIndexer: false, DeclaringSyntaxReferences.Length: 1 } property =>
                property.DeclaringSyntaxReferences[0].GetSyntax(ct) is PropertyDeclarationSyntax { Initializer: null } declaration && Symbols.IsAutoPropertyDeclaration(declaration),

            _ => false,
        };

        if (!declared || member.ContainingType is not { TypeKind: TypeKind.Class } type || !type.DeclaringSyntaxReferences.All(r => compilation.SyntaxTrees.Contains(r.SyntaxTree)))
        {
            return null;
        }

        var setsBy = new Dictionary<ConstructorDeclarationSyntax, int>();
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.IsImplicitlyDeclared)
            {
                if (type.IsRecord && constructor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type))
                {
                    continue;
                }

                return null; // a default constructor, which sets nothing
            }

            if (constructor.DeclaringSyntaxReferences.Length != 1 || constructor.DeclaringSyntaxReferences[0].GetSyntax(ct) is not ConstructorDeclarationSyntax declaration)
            {
                return null; // a primary constructor, which can't set it
            }

            setsBy[declaration] = 0;
        }

        var assignments = new List<(IOperation, SemanticModel)>();
        foreach (var part in type.DeclaringSyntaxReferences)
        {
            var partModel = compilation.GetSemanticModel(part.SyntaxTree);
            foreach (var name in Symbols.References(member, part.GetSyntax(ct), partModel, ct, byDefinition: true))
            {
                if (WriteAnalysis.TakesReference(name, partModel, ct))
                {
                    return null;
                }

                if (!WriteAnalysis.IsWrite(name, partModel, ct) && !WriteAnalysis.IsTargetOfMemberWrite(name))
                {
                    continue;
                }

                // `_pet = e;` or `this._pet = e;`: a statement of a constructor's body, or its expression body.
                ExpressionSyntax target = name.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access && access.Name == name ? access : name;
                if (target.Parent is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment || assignment.Left != target)
                {
                    return null;
                }

                var constructor = assignment.Parent switch
                {
                    ExpressionStatementSyntax { Parent: BlockSyntax { Parent: ConstructorDeclarationSyntax inBody } } => inBody,
                    ArrowExpressionClauseSyntax { Parent: ConstructorDeclarationSyntax byArrow } => byArrow,
                    _ => null,
                };

                if (constructor is null || !setsBy.ContainsKey(constructor) || partModel.GetOperation(assignment, ct) is not ISimpleAssignmentOperation { Value: var value })
                {
                    return null;
                }

                setsBy[constructor]++;
                assignments.Add((value, partModel));
            }
        }

        return setsBy.All(pair => pair.Value == (pair.Key.Initializer is { RawKind: (int)SyntaxKind.ThisConstructorInitializer } ? 0 : 1)) ? assignments : null;
    }

    /// <summary>
    /// The value a statement copies into the tracked value, if that's all it does to it:
    /// <c>var local = other;</c> or <c>local = other;</c>, with nothing converted, or a deconstruction like <c>var (local, n) = (other, 1);</c>.
    /// </summary>
    private static TrackedValue? CopySource(StatementSyntax statement, TrackedValue value, SemanticModel model, CancellationToken ct)
    {
        if (statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: DeclarationExpressionSyntax or TupleExpressionSyntax } deconstruction })
        {
            return DeconstructionSource(deconstruction, value, model, ct) is { } part && !SymbolEqualityComparer.Default.Equals(part.Variable, value.Variable) ? value.WithRoot(part) : null;
        }

        var copied = statement switch
        {
            LocalDeclarationStatementSyntax declaration => declaration.Declaration.Variables.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(v, ct), value.Variable))?.Initializer?.Value,
            ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment }
                when SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, ct).Symbol, value.Variable) => assignment.Right,
            _ => null,
        };

        return copied is not null
            && TrackedValue.From(model.GetOperation(copied, ct), value.Session) is { } source
            && !SymbolEqualityComparer.Default.Equals(source.Variable, value.Variable)
                ? value.WithRoot(source)
                : null;
    }

    /// <summary>
    /// For a deconstruction that assigns the value's root variable, what it assigns it from:
    /// the matching element of a tuple literal, an element of a followed tuple (<c>t.Item1</c>), or the property a record's generated Deconstruct reads (<c>owner.Pet</c>).
    /// </summary>
    private static TrackedValue? DeconstructionSource(AssignmentExpressionSyntax assignment, TrackedValue value, SemanticModel model, CancellationToken ct)
    {
        if (model.GetOperation(assignment, ct) is not IDeconstructionAssignmentOperation deconstruction)
        {
            return null;
        }

        var target = deconstruction.Target is IDeclarationExpressionOperation declaration ? declaration.Expression : deconstruction.Target;
        if (PathTo(target, value.Variable) is not { } path)
        {
            return null;
        }

        var info = model.GetDeconstructionInfo(assignment);
        var source = Sequences.Unconverted(deconstruction.Value);
        TrackedValue? followed = null;
        var type = source.Type;

        foreach (var index in path)
        {
            if (followed is null && source is ITupleOperation literal && index < literal.Elements.Length)
            {
                source = Sequences.Unconverted(literal.Elements[index]);
                type = source.Type;
            }
            else
            {
                followed ??= TrackedValue.From(source, value.Session);
                var member = Facts.DeconstructedMember(type, info.Method, index);
                
                if (followed is null || member is null || followed.WithMember(member, model.Compilation) is not { } next)
                {
                    return null;
                }

                followed = next;
                type = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
            }

            info = index < info.Nested.Length ? info.Nested[index] : default;
        }

        return followed ?? TrackedValue.From(source, value.Session);
    }

    private static CaseSet FactsAtEndOfAssignment(StatementSyntax statement, Context context)
    {
        var model = context.Model;
        var ct = context.CancellationToken;
        var value = context.Value;

        switch (statement)
        {
            // `pet = e;` (or `box.Pet = e;`, `owner = e;` for owner.Pet), where e doesn't write it too.
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment }
                when model.GetOperation(assignment, ct) is ISimpleAssignmentOperation { Target: var target, Value: var assigned }
                    && (value.Variable is not null || Functions.IsInConstruction(statement)) && TrackedValue.From(target, value.Session) is { } assignedTo
                    && assignedTo.IsPrefixOf(value)
                    && !context.Writes(assignment.Right):
                return KnownExcluded(assigned, [.. value.Steps.Skip(assignedTo.Steps.Length)], context.Space, model, value.Session, ct, context.Copies + 1);

            // `Pet pet = e;`
            case LocalDeclarationStatementSyntax declaration:
                var declarator = declaration.Declaration.Variables.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(v, ct), value.Variable));
                return declarator is { Initializer.Value: var initializer }
                    && model.GetOperation(declarator, ct) is IVariableDeclaratorOperation { Initializer.Value: { } initial }
                    && !context.Writes(initializer)
                    && declaration.Declaration.Variables.All(v => v == declarator || v.Initializer is null || !context.Writes(v.Initializer.Value))
                    ? KnownExcluded(initial, value.Steps, context.Space, model, value.Session, ct, context.Copies + 1)
                    : CaseSet.Empty;

            // What holds at a labeled statement's end comes from its own branches, and from what holds on the way in,
            // which takes the label into account.
            case LabeledStatementSyntax labeled:
                return FactsAtEndOfAssignment(labeled.Statement, context);

            case BlockSyntax block when DisposesAtEnd(block, context):
                return CaseSet.Empty;

            case BlockSyntax block:
                var atEnd = CaseSet.Empty;
                for (var i = block.Statements.Count - 1; i >= 0; i--)
                {
                    var inner = block.Statements[i];
                    if (inner is LabeledStatementSyntax)
                    {
                        return CaseSet.Empty;
                    }

                    if (context.Writes(inner))
                    {
                        return atEnd | FactsAtEndOfWrite(inner, context);
                    }

                    atEnd |= FactsAtEnd(inner, context);
                }

                return atEnd;

            // `x ??= e;`: x as it was, if that wasn't null, or else e.
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceAssignmentExpression } coalesce }
                when model.GetOperation(coalesce, ct) is ICoalesceAssignmentOperation { Target: var target, Value: var assigned }
                    && TrackedValue.From(target, value.Session) is { } assignedTo && assignedTo.IsPrefixOf(value)
                    && !context.Writes(coalesce.Right):
                var kept = Excluded(statement, value, context.Space, model, ct, onEntry: true, context.Copies + 1)
                    | (assignedTo.Steps.Length == value.Steps.Length && context.Space.NullOnlyAsNoValue ? CaseSet.Null : CaseSet.Empty);
                return kept & KnownExcluded(assigned, [.. value.Steps.Skip(assignedTo.Steps.Length)], context.Space, model, value.Session, ct, context.Copies + 1);

            // Control leaves a switch statement through a break, with what held there, or, with no default, when no label
            // matched, with what held on the way in.
            case SwitchStatementSyntax switchStatement when !context.Writes(switchStatement.Expression)
                && model.GetOperation(switchStatement, ct) is ISwitchOperation switchOp:
                var atEndOfSwitch = AtBreaks(switchStatement, context);
                if (!Statements.HasDefault(switchOp))
                {
                    var whenClausesWrite = switchStatement.Sections.SelectMany(section => section.Labels).OfType<CasePatternSwitchLabelSyntax>()
                        .Any(label => label.WhenClause is { } when && context.Writes(when.Condition));
                    atEndOfSwitch = Meet(atEndOfSwitch, whenClausesWrite ? CaseSet.Empty
                        : Excluded(switchStatement, value, context.Space, model, ct, onEntry: true, context.Copies + 1) | NoneMatched(switchOp, context)
                    );
                }

                return atEndOfSwitch ?? CaseSet.Empty;

            // The end of a try is reached through the end of its block or of a catch, and then its finally. A catch can
            // start anywhere in the block, so one that doesn't write the value only knows something if the block doesn't.
            case TryStatementSyntax tryStatement:
                CaseSet? tryOnEntry = null;
                CaseSet TryOnEntry() =>
                    tryOnEntry ??= Excluded(tryStatement, value, context.Space, model, ct, onEntry: true, context.Copies + 1);
                var blockWrites = context.Writes(tryStatement.Block);

                CaseSet? throughTry = context.Exits(tryStatement.Block) ? null
                    : blockWrites ? FactsAtEndOfWrite(tryStatement.Block, context)
                    : FactsAtEnd(tryStatement.Block, context) | TryOnEntry();
                foreach (var catchClause in tryStatement.Catches)
                {
                    throughTry = Meet(throughTry, context.Exits(catchClause.Block) ? null
                        : context.Writes(catchClause.Block) ? FactsAtEndOfWrite(catchClause.Block, context)
                        : blockWrites || (catchClause.Filter is { } filter && context.Writes(filter.FilterExpression)) ? CaseSet.Empty
                        : FactsAtEnd(catchClause.Block, context) | TryOnEntry()
                    );
                }

                if (tryStatement.Finally is not { Block: var finallyBlock })
                {
                    return throughTry ?? CaseSet.Empty;
                }

                return context.Writes(finallyBlock) ? FactsAtEndOfWrite(finallyBlock, context) : (throughTry ?? CaseSet.Empty) | FactsAtEnd(finallyBlock, context);

            case IfStatementSyntax ifStatement when !context.Writes(ifStatement.Condition):
                CaseSet? onEntry = null;
                CaseSet OnEntry() =>
                    onEntry ??= Excluded(ifStatement, value, context.Space, model, ct, onEntry: true, context.Copies + 1);

                var condition = context.Condition(ifStatement.Condition);
                CaseSet? Branch(StatementSyntax? branch, CaseSet implied) =>
                    branch is null ? implied | OnEntry()
                    : context.Exits(branch) ? null
                    : context.Writes(branch) ? FactsAtEndOfWrite(branch, context)
                    : implied | FactsAtEnd(branch, context) | OnEntry();

                return Meet(Branch(ifStatement.Statement, condition.AlwaysFalse), Branch(ifStatement.Else?.Statement, condition.AlwaysTrue)) ?? CaseSet.Empty;

            default:
                return CaseSet.Empty;
        }
    }

    /// <summary>
    /// What holds at the end of a statement that writes the value:
    /// for an assignment (or declaration) of it, what's known about what it's assigned;
    /// for a block, what its statements from its last write on establish;
    /// for an <c>if</c>, what holds at the end of every branch that falls through, where one that doesn't write gets what held on the way into the <c>if</c>, along with what its condition implies.
    /// </summary>
    private static CaseSet FactsAtEndOfWrite(StatementSyntax statement, Context context) =>
        FactsAtEndOfAssignment(statement, context) | FactsFromOutArgument(statement, context);

    /// <summary>
    /// If the write the walk stopped at copies another value into this one (<c>var local = pet;</c>), what's known about that value carries over:
    /// what held for it there, or, if nothing writes it between the copy and where the walk started (so the two still match), everything that holds for it there.
    /// </summary>
    private static CaseSet FactsFromCopy(StoppedAtWrite stop, Context context)
    {
        if (CopySource(stop.Write, context.Value, context.Model, context.CancellationToken) is not { } source)
        {
            return CaseSet.Empty;
        }

        var model = context.Model;
        var ct = context.CancellationToken;
        var sourceContext = new Context(source, context.Space, model, context.Start, onEntry: false, usage: null, ct);
        var unchanged = !stop.Statements.Skip(stop.WriteIndex + 1).Take(stop.ChildIndex - stop.WriteIndex).Any(sourceContext.Writes);

        if (unchanged && IsTrackable(source, context.Start, model, ct))
        {
            return Excluded(context.Start, source, context.Space, model, ct, onEntry: false, context.Copies + 1);
        }

        return IsTrackable(source, stop.Write, model, ct)
            ? Excluded(stop.Write, source, context.Space, model, ct, onEntry: true, context.Copies + 1)
            : CaseSet.Empty;
    }

    /// <summary>
    /// For a variable a <c>var</c> pattern binds to a whole value (<c>var other =&gt; ...</c>, <c>x is var copy</c>, <c>case var x:</c>):
    /// what's known about that value carries over, if nothing writes either in between.
    /// </summary>
    private static CaseSet FactsFromDesignation(VariableUsage usage, Context context)
    {
        // `var x` is a var pattern in some places and a declaration pattern with type `var` in others (arms, labels).
        // A typed designation (`owner is { Pet: not Bird } o`, `x is Owner o`) binds the same object too; one that takes
        // a case out of a union (`r is Err e`) binds what it holds, whose members are the case's.
        if (usage.Writes.Count > 0
            || usage.Declarations.Count != 1
            || usage.Declarations[0] is not SingleVariableDesignationSyntax { Parent: VarPatternSyntax or DeclarationPatternSyntax or RecursivePatternSyntax } designation
        )
        {
            return CaseSet.Empty;
        }

        var bindsWhole = designation.Parent is VarPatternSyntax or DeclarationPatternSyntax { Type.IsVar: true } || BindsSameValue(designation);
        if (!bindsWhole && !TakesCase(designation))
        {
            return CaseSet.Empty;
        }

        // Up through `and` (an `or` can't declare variables), and through the subpatterns it's in, collecting the members
        // it's read through (`{ Pet: var p }` binds `owner.Pet`), to the pattern's owner.
        var model = context.Model;
        var ct = context.CancellationToken;
        var steps = new List<Step>();
        SyntaxNode pattern = designation.Parent!;

        // The variable is only assigned where the whole pattern matched, so what it's `and`-ed with on the same value
        // holds for it wherever it's read (`[(Plain or Para) and var head, ..]`).
        return AndedWith(pattern) | FromSource();

        CaseSet AndedWith(SyntaxNode bound)
        {
            if (!context.Value.Steps.IsEmpty || model.GetOperation(bound, ct) is not IPatternOperation { InputType: var boundType })
            {
                return CaseSet.Empty;
            }

            var excluded = CaseSet.Empty;
            for (var inner = bound; inner.Parent is ParenthesizedPatternSyntax or BinaryPatternSyntax { RawKind: (int)SyntaxKind.AndPattern }; inner = inner.Parent)
            {
                if (inner.Parent is BinaryPatternSyntax both
                    && model.GetOperation(Unparenthesized(both.Left == inner ? both.Right : both.Left), ct) is IPatternOperation sibling
                    && SymbolEqualityComparer.Default.Equals(sibling.InputType, boundType)
                )
                {
                    excluded |= Facts.Of(sibling, context.Space, model.Compilation).AlwaysFalse;
                }
            }

            return excluded;
        }

        static PatternSyntax Unparenthesized(PatternSyntax pattern) =>
            pattern is ParenthesizedPatternSyntax parenthesized ? Unparenthesized(parenthesized.Pattern) : pattern;

        CaseSet FromSource()
        {
            while (true)
            {
                var narrowedInput = (model.GetOperation(pattern, ct) as IPatternOperation)?.InputType;
                while (pattern.Parent is ParenthesizedPatternSyntax || pattern.Parent is BinaryPatternSyntax { RawKind: (int)SyntaxKind.AndPattern } || pattern.Parent is UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern })
                {
                    pattern = pattern.Parent;
                }

                // `item is Val and Arr a`, on a union that holds a union: the left of the `and` takes the case out, so the
                // right reads what that case holds, not what the input does.
                if (model.GetOperation(pattern, ct) is IPatternOperation { InputType: var outerInput }
                    && (Symbols.UnderlyingOfNullable(outerInput) ?? outerInput).IsUnion
                    && !SymbolEqualityComparer.Default.Equals(narrowedInput, outerInput)
                )
                {
                    return CaseSet.Empty;
                }

                if (pattern.Parent is not SubpatternSyntax { Parent: { Parent: RecursivePatternSyntax recursiveSyntax } clause } subpattern
                    || model.GetOperation(recursiveSyntax, ct) is not IRecursivePatternOperation recursive
                )
                {
                    break;
                }

                IReadOnlyList<Step> members = clause is PositionalPatternClauseSyntax positional
                    ? Facts.PositionalMember(recursive, positional.Subpatterns.IndexOf(subpattern)) is { } member ? [new MemberStep(member)] : []
                    : model.GetOperation(subpattern, ct) is IPropertySubpatternOperation property ? Facts.MemberChain(property.Member) : [];
                if (members.Count == 0)
                {
                    return CaseSet.Empty;
                }

                // The members of a union's case are read from the case it holds.
                var narrowed = recursive.InputType.IsUnion && !SymbolEqualityComparer.Default.Equals(recursive.MatchedType, recursive.InputType);
                steps.InsertRange(0, narrowed ? [new CaseStep(recursive.MatchedType), .. members] : members);
                pattern = recursiveSyntax;
            }

            var (input, region) = pattern.Parent switch
            {
                IsPatternExpressionSyntax isPattern => (isPattern.Expression, (SyntaxNode?)isPattern.FirstAncestorOrSelf<StatementSyntax>() ?? Functions.BodyExpression(isPattern)),
                SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax outer } arm => (outer.GoverningExpression, arm),
                CasePatternSwitchLabelSyntax { Parent: SwitchSectionSyntax { Parent: SwitchStatementSyntax switchStatement } section } => (switchStatement.Expression, section),
                _ => (null, null),
            };

            // A positional pattern on a tuple literal reads its element: `(a, b) is (var x, _)` binds a.
            var inputOperation = input is null ? null : model.GetOperation(input, ct);
            while (inputOperation is IConversionOperation { IsImplicit: true } conversion)
            {
                inputOperation = conversion.Operand;
            }

            if (inputOperation is ITupleOperation tuple && steps.Count > 0
                && steps[0] is MemberStep { Member: IFieldSymbol { CorrespondingTupleField: { } or { ContainingType.IsTupleType: true } } element }
            )
            {
                inputOperation = Sequences.ElementIndex(tuple, element) is var index and >= 0 ? tuple.Elements[index] : null;
                steps.RemoveAt(0);
            }

            if (input is null || region is null || UpTo(region, context.Start) is not { } parts
                || TrackedValue.From(inputOperation, context.Value.Session) is not { } whole
                || parts.Any(part => WritesIn(part, whole))
                || whole.Through(steps, model.Compilation, followsMutableState: false) is not { } source
                || !IsTrackable(source, context.Start, model, ct)
            )
            {
                return CaseSet.Empty;
            }

            var excluded = bindsWhole ? Excluded(context.Start, context.Value.WithRoot(source), context.Space, model, ct, onEntry: false, context.Copies + 1) : CaseSet.Empty;

            // `owner is not Shelter shelter` makes shelter owner as a Shelter: what's known about owner's members holds for
            // shelter's, and so does what's known about them when owner is a Shelter (`owner is Shelter { Pet: Bird }`).
            if (steps.Count == 0 && !context.Value.Steps.IsEmpty
                && model.GetDeclaredSymbol(designation, ct) is ILocalSymbol { Type: var bound } && inputOperation?.Type is { } inputType && !SymbolEqualityComparer.Default.Equals(bound, inputType)
                && context.Value.WithRoot(source.Through([new CaseStep(bound)], model.Compilation, followsMutableState: false)!) is var asBound && IsTrackable(asBound, context.Start, model, ct)
            )
            {
                excluded |= Excluded(context.Start, asBound, context.Space, model, ct, onEntry: false, context.Copies + 1);
            }

            return excluded;
        }

        // Where the variable has its value, up to where the walk started: the statement (or arm, or section) that binds it,
        // and for a statement that doesn't hold the start (`if (o is not Shelter shelter) return;`), the statements after
        // it in its list up to the one that does.
        static IReadOnlyList<SyntaxNode>? UpTo(SyntaxNode region, SyntaxNode start)
        {
            if (region.Span.Contains(start.Span))
            {
                return [region];
            }

            if (region is not StatementSyntax statement || Statements.StatementsAround(region.Parent!) is not { } statements)
            {
                return null;
            }

            var from = Statements.IndexOf(statements, statement);
            var to = statements.ToList().FindIndex(s => s.Span.Contains(start.Span));

            return from >= 0 && to > from ? [.. statements.Skip(from).Take(to - from + 1)] : null;
        }

        // The pattern's input is a union, and the variable is one of its cases (or a type its cases derive from).
        bool TakesCase(SingleVariableDesignationSyntax typed) =>
            context.Model.GetOperation(typed.Parent!, context.CancellationToken) is IPatternOperation { InputType: var input }
            && (Symbols.UnderlyingOfNullable(input) ?? input).IsUnion
            && context.Model.GetDeclaredSymbol(typed, context.CancellationToken) is ILocalSymbol { Type.IsUnion: false };

        // The pattern's input isn't a union (whose patterns bind its case), and the variable is the input itself, or a
        // reference to the same object.
        bool BindsSameValue(SingleVariableDesignationSyntax typed)
        {
            if (context.Model.GetOperation(typed.Parent!, context.CancellationToken) is not IPatternOperation { InputType: var input }
                || context.Model.GetDeclaredSymbol(typed, context.CancellationToken) is not ILocalSymbol { Type: var bound }
                || input.IsUnion
                || input.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            )
            {
                return false;
            }

            var conversion = context.Model.Compilation.ClassifyCommonConversion(input, bound);
            return conversion.IsIdentity || (input.IsReferenceType && conversion.IsReference);
        }

        bool WritesIn(SyntaxNode part, TrackedValue value) =>
            part switch
            {
                SwitchExpressionArmSyntax arm => (arm.WhenClause is { } when && WriteAnalysis.Writes(when.Condition, value, context.Model, context.CancellationToken)) || WriteAnalysis.Writes(arm.Expression, value, context.Model, context.CancellationToken),
                SwitchSectionSyntax section => section.Statements.Any(statement => WriteAnalysis.Writes(statement, value, context.Model, context.CancellationToken)),
                _ => WriteAnalysis.Writes(part, value, context.Model, context.CancellationToken),
            };
    }

    /// <summary>
    /// For a local that's written after its declaration (in a loop, say, where what held before the loop doesn't hold in it):
    /// what every value it's ever given is known not to be, which it can't be anywhere.
    /// Only for a local whose every write is its declaration's initializer or a plain assignment (<see cref="VariableUsage"/> has already made sure they're all in plain sight).
    /// </summary>
    private static CaseSet FactsFromEveryWrite(VariableUsage usage, Context context)
    {
        if (usage.Writes.Count == 0 || context.Value.Variable is not ILocalSymbol || context.Value.IsExternallyMutable)
        {
            return CaseSet.Empty;
        }

        var model = context.Model;
        var ct = context.CancellationToken;
        var given = new List<IOperation?>();

        foreach (var declaration in usage.Declarations)
        {
            if (declaration is not VariableDeclaratorSyntax declarator)
            {
                return CaseSet.Empty; // a designation or foreach variable, which gets a value of its own kind
            }

            if (declarator.Initializer is not null)
            {
                given.Add((model.GetOperation(declarator, ct) as IVariableDeclaratorOperation)?.Initializer?.Value);
            }
        }

        given.AddRange(usage.Writes.Select(write =>
            write.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment && assignment.Left == write
                ? (model.GetOperation(assignment, ct) as ISimpleAssignmentOperation)?.Value
                : null
        ));

        // A write that switches on the variable itself (`state = state switch { Waiting => new Running(), Running r => r,
        // Done d => d };`) gives what its arms do, for what the variable can be then: what's ruled out everywhere is what
        // the other writes rule out, and what these keep ruled out, given that it was (by induction over the writes).
        CaseSet? everywhere = null;
        var steps = new List<ISwitchExpressionOperation>();
        foreach (var value in given)
        {
            if (value is null)
            {
                return CaseSet.Empty;
            }

            if (context.Value.Steps.IsEmpty && Unconverted(value) is ISwitchExpressionOperation step && context.Value.IsReadBy(step.Value))
            {
                steps.Add(step);
                continue;
            }

            everywhere = Meet(everywhere, KnownExcluded(value, context.Value.Steps, context.Space, model, context.Value.Session, ct, context.Copies + 1));
        }

        var invariant = everywhere ?? CaseSet.Empty;
        for (var round = 0; steps.Count > 0 && round <= context.Space.Cases.Length; round++)
        {
            var kept = steps.Aggregate(invariant, (excluded, step) => excluded & Stepped(step, invariant));
            if (kept.Cases.Count == invariant.Cases.Count && kept.IncludesNull == invariant.IncludesNull)
            {
                return invariant;
            }

            invariant = kept;
        }

        return steps.Count == 0 ? invariant : CaseSet.Empty;

        // What the switch gives, where its input is none of `before`: what every arm that can match a case that's left
        // gives, and for an arm that gives the input back, the cases that are left and that it can match.
        CaseSet Stepped(ISwitchExpressionOperation step, CaseSet before)
        {
            CaseSet? given = null;
            var earlier = CaseSet.Empty;
            foreach (var arm in step.Arms)
            {
                var outcome = Facts.Of(arm.Pattern, context.Space, model.Compilation);
                var left = context.Space.Cases.Cast<object?>().Append(null).Where(c => !before.ContainsCase(c) && !earlier.ContainsCase(c));
                if (left.All(outcome.AlwaysFalse.ContainsCase) || Facts.IsThrow(arm.Value))
                {
                    continue;
                }

                given = Meet(given, context.Value.IsReadBy(Unconverted(arm.Value))
                    ? before | earlier | outcome.AlwaysFalse
                    : KnownExcluded(arm.Value, [], context.Space, model, context.Value.Session, ct, context.Copies + 1));

                if (arm.Guard is null)
                {
                    earlier |= outcome.AlwaysTrue;
                }
            }

            return given ?? CaseSet.Of(context.Space.Cases, includesNull: true);
        }

        // Through identity conversions, and a target-typed switch's.
        static IOperation Unconverted(IOperation operation) =>
            operation is IConversionOperation { OperatorMethod: null } conversion && (conversion.Conversion.IsIdentity || conversion.GetConversion().IsSwitchExpression)
                ? Unconverted(conversion.Operand)
                : operation;
    }

    /// <summary>
    /// For a value read through a readonly field or get-only auto-property that only its initializer ever sets (nothing in its type assigns it, not even a constructor):
    /// what's known about the initializer's value, followed by the rest of the path.
    /// <c>static readonly Pet Favourite = new Cat("Tom");</c> is a Cat wherever it's read.
    /// </summary>
    private static CaseSet FactsFromInitializers(TrackedValue value, CaseSpace space, SemanticModel model, CancellationToken ct, int copies)
    {
        var excluded = CaseSet.Empty;
        for (var i = 0; i < value.Steps.Length; i++)
        {
            if (value.Steps[i] is not MemberStep { Member: var member })
            {
                continue;
            }

            var rest = value.Steps.Skip(i + 1).ToList();
            var declaringSpace = space.Reading(member, model.Compilation);
            if (OnlyInitializer(member, model.Compilation, ct) is var (initializer, initializerModel))
            {
                excluded |= KnownExcluded(initializer, rest, declaringSpace, initializerModel, value.Session, ct, copies + 1);
            }
            else if (ConstructorAssignments(member, model.Compilation, ct) is { Count: > 0 } assignments)
            {
                // What every constructor sets it to. Not null, though: something the constructor calls before it sets the
                // member would read the default.
                CaseSet? set = null;
                foreach (var (assigned, assignedModel) in assignments)
                {
                    set = Meet(set, KnownExcluded(assigned, rest, declaringSpace, assignedModel, value.Session, ct, copies + 1));
                }

                excluded |= CaseSet.Of((set ?? CaseSet.Empty).Cases);
            }
        }

        return excluded;
    }

    /// <summary>
    /// For a statement that sets the value's variable as a call's <c>out</c> argument (<c>TryCat(out var pet);</c>, <c>if (!TryCat(out var pet)) return;</c>), and doesn't otherwise write it:
    /// what every assignment the method makes to that parameter says, for a method declared here that can't be overridden.
    /// </summary>
    private static CaseSet FactsFromOutArgument(StatementSyntax statement, Context context)
    {
        var model = context.Model;
        var ct = context.CancellationToken;
        var value = context.Value;
        if (value.Variable is not ILocalSymbol local)
        {
            return CaseSet.Empty;
        }

        // Where the call is: an expression statement, a declaration's initializers, or an if's condition (whose branches
        // can't write the variable then).
        IOperation? evaluated = statement switch
        {
            ExpressionStatementSyntax or LocalDeclarationStatementSyntax => model.GetOperation(statement, ct),
            IfStatementSyntax ifStatement when !context.Writes(ifStatement.Statement) && (ifStatement.Else is null || !context.Writes(ifStatement.Else.Statement)) =>
                model.GetOperation(ifStatement.Condition, ct),
            _ => null,
        };

        var outArguments = evaluated?.DescendantsAndSelf().OfType<IArgumentOperation>()
            .Where(a => a.Parameter?.RefKind == RefKind.Out && a.Value switch
            {
                ILocalReferenceOperation reference => SymbolEqualityComparer.Default.Equals(reference.Local, local),
                IDeclarationExpressionOperation { Expression: ILocalReferenceOperation declared } => SymbolEqualityComparer.Default.Equals(declared.Local, local),
                _ => false,
            })
            .ToList();

        if (outArguments is not { Count: 1 }
            || outArguments[0].Parent is not IInvocationOperation { TargetMethod: var method } call
            || Functions.IsIntercepted(call)
            || outArguments[0].Parameter is not { } outParameter
            || Symbols.References(local, statement, model, ct).Any(name =>
                WriteAnalysis.IsWrite(name, model, ct) && !name.Span.IntersectsWith(outArguments[0].Syntax.Span)
            )
        )
        {
            return CaseSet.Empty;
        }

        var calleeSpace = context.Space.Reading(method, model.Compilation);
        method = method.OriginalDefinition;
        if ((!value.Session.FollowsCallees && method.MethodKind != MethodKind.LocalFunction) || Functions.OwnDeclaration(method, model.Compilation, ct) is not { } declaration)
        {
            return CaseSet.Empty;
        }

        var calleeModel = Functions.SemanticModelFor(declaration, model);
        var parameter = method.Parameters[outParameter.Ordinal];
        CaseSet? assigned = null;
        foreach (var name in Symbols.References(parameter, declaration, calleeModel, ct, descendInto: n => n == declaration || !Functions.IsFunction(n)))
        {
            // Every use is either a read or a plain assignment; anything else could set it to anything.
            if (calleeModel.GetOperation(name, ct) is { Parent: ISimpleAssignmentOperation assignment } reference && assignment.Target == reference)
            {
                assigned = Meet(assigned, KnownExcluded(assignment.Value, value.Steps, calleeSpace, calleeModel, value.Session, ct, context.Copies + 1));
            }
            else if (WriteAnalysis.IsWrite(name, calleeModel, ct) || WriteAnalysis.TakesReference(name, calleeModel, ct))
            {
                return CaseSet.Empty;
            }
        }

        return assigned ?? CaseSet.Empty;
    }

    /// <summary>
    /// The members a deconstruction reads, element index by element index, from a value of <paramref name="type"/>:
    /// a tuple's elements, or the properties a record's generated Deconstruct reads.
    /// </summary>
    private static List<Step>? MembersAlong(IReadOnlyList<int> path, ITypeSymbol? type, DeconstructionInfo info, Session session)
    {
        var members = new List<Step>();
        foreach (var index in path)
        {
            var member = Facts.DeconstructedMember(type, info.Method, index);
            if (member is null || !TrackedValue.IsStableMember(member, null, session))
            {
                return null;
            }

            members.Add(new MemberStep(member));
            type = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
            info = index < info.Nested.Length ? info.Nested[index] : default;
        }

        return members;
    }

    /// <summary>
    /// The initializer of a readonly field or get-only auto-property that nothing else assigns.
    /// </summary>
    private static (IOperation Initializer, SemanticModel Model)? OnlyInitializer(ISymbol member, Compilation compilation, CancellationToken ct)
    {
        var declarator = member switch
        {
            IFieldSymbol { IsReadOnly: true, IsConst: false, DeclaringSyntaxReferences.Length: 1 } field =>
                field.DeclaringSyntaxReferences[0].GetSyntax(ct) is VariableDeclaratorSyntax { Initializer: { } fieldInitializer } ? fieldInitializer : null,
            IPropertySymbol { SetMethod: null, IsAbstract: false, IsVirtual: false, IsOverride: false, DeclaringSyntaxReferences.Length: 1 } property =>
                property.DeclaringSyntaxReferences[0].GetSyntax(ct) is PropertyDeclarationSyntax { Initializer: { } propertyInitializer } declaration
                    && Symbols.IsAutoPropertyDeclaration(declaration)
                    ? propertyInitializer
                    : null,
            _ => null,
        };

        if (declarator is null || member.ContainingType is not { } type || !compilation.SyntaxTrees.Contains(declarator.SyntaxTree))
        {
            return null;
        }

        // Nothing in the type (constructors, init accessors, anything) assigns it, or takes a reference to it.
        foreach (var part in type.DeclaringSyntaxReferences)
        {
            var partModel = compilation.GetSemanticModel(part.SyntaxTree);
            if (Symbols.References(member, part.GetSyntax(ct), partModel, ct, byDefinition: true)
                .Any(name => WriteAnalysis.IsWrite(name, partModel, ct) || WriteAnalysis.TakesReference(name, partModel, ct) || WriteAnalysis.IsTargetOfMemberWrite(name))
            )
            {
                return null;
            }
        }

        var model = compilation.GetSemanticModel(declarator.SyntaxTree);
        return model.GetOperation(declarator, ct) is ISymbolInitializerOperation { Value: { } initial } ? (initial, model) : null;
    }

    /// <summary>
    /// The element indexes, outermost first, of the variable in a (nested) tuple of deconstruction targets.
    /// </summary>
    private static List<int>? PathTo(IOperation target, ISymbol? variable)
    {
        if (target is ILocalReferenceOperation local)
        {
            return SymbolEqualityComparer.Default.Equals(local.Local, variable) ? [] : null;
        }

        if (target is IDeclarationExpressionOperation inner)
        {
            return PathTo(inner.Expression, variable);
        }

        if (target is ITupleOperation tuple)
        {
            for (var i = 0; i < tuple.Elements.Length; i++)
            {
                if (PathTo(tuple.Elements[i], variable) is { } rest)
                {
                    return [i, .. rest];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What the value of an expression can't be, as far as can be told from the expression itself, followed by <paramref name="steps"/>:
    /// a union made from a case (<c>new Cat(...)</c> converted to Pet) is only that case;
    /// a conditional is what both branches are;
    /// a followed value is what's ruled out for it there;
    /// a call to a method of one's own is what all its returns are;
    /// and a member of a new object is what it was set to.
    /// </summary>
    public static CaseSet KnownExcluded(IOperation expression, IReadOnlyList<Step> steps, CaseSpace space, SemanticModel model, Session session, CancellationToken ct, int copies)
    {
        if (copies > Limits.Copies)
        {
            return CaseSet.Empty;
        }

        // Parts of the expression (branches, arms) are smaller expressions, so following them can't go on forever: only
        // starting another walk counts against the limit.
        CaseSet Known(IOperation part) =>
            KnownExcluded(part, steps, space, model, session, ct, copies);

        // Through identity conversions, and the conversions of a target-typed `?:` or switch (whose branches are
        // converted themselves).
        while (expression is IConversionOperation { OperatorMethod: null } passThrough
            && (passThrough.Conversion.IsIdentity || passThrough.GetConversion() is { IsConditionalExpression: true } or { IsSwitchExpression: true })
        )
        {
            expression = passThrough.Operand;
        }

        if (steps.Count == 0)
        {
            switch (expression)
            {
                // `null` (or `default` of a reference type) holds no case at all.
                case { ConstantValue: { HasValue: true, Value: null } }:
                    return CaseSet.Of(space.Cases);

                // A union made from one of its cases (`Pet pet = cat;`, `new Pet(cat)`), or a closed type from one of its
                // derived types (`Shape shape = new Circle();`).
                case IConversionOperation { OperatorMethod: IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.IsUnion: true } } union:
                    return FromCase(union.Operand);
                case IObjectCreationOperation { Constructor: { ContainingType.IsUnion: true, Parameters.Length: 1 } } creation when creation.Arguments.Length == 1:
                    return FromCase(creation.Arguments[0].Value);
                case IConversionOperation { Conversion.IsImplicit: true, Conversion.IsReference: true } reference when !space.IsUnion:
                    return FromCase(reference.Operand);

                // `Pet? maybe = pet;`: the union inside, which is there.
                case IConversionOperation { OperatorMethod: null, Type: INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped, Operand: var inside }
                    when SymbolEqualityComparer.Default.Equals(wrapped.TypeArguments[0], inside.Type):
                    return Known(inside) | (space.NullOnlyAsNoValue ? CaseSet.Null : CaseSet.Empty);
            }
        }

        switch (expression)
        {
            // A branch that throws gives no value: `flag ? a : throw ...` is what `a` is.
            case IConditionalOperation { WhenFalse: { } whenFalse } conditional:
                return Meet(Facts.IsThrow(conditional.WhenTrue) ? null : Known(conditional.WhenTrue), Facts.IsThrow(whenFalse) ? null : Known(whenFalse)) ?? CaseSet.Empty;

            // `pet switch { Bird => new Cat(...), var other => other }`: what every arm that gives a value gives, each
            // read in its arm (so `other` isn't a Bird).
            case ISwitchExpressionOperation switchExpression:
                CaseSet? fromArms = null;
                foreach (var arm in switchExpression.Arms)
                {
                    if (!Facts.IsThrow(arm.Value))
                    {
                        fromArms = Meet(fromArms, Known(arm.Value));
                    }
                }

                return fromArms ?? CaseSet.Empty;

            // `x ?? throw ...` is x, and isn't null (where null can only mean nothing there).
            case ICoalesceOperation coalesce when steps.Count == 0 && Facts.IsThrow(coalesce.WhenNull):
                return Known(coalesce.Value) | (space.NullOnlyAsNoValue ? CaseSet.Null : CaseSet.Empty);

            case ICoalesceOperation coalesce when steps.Count == 0:
                return Known(coalesce.Value) & Known(coalesce.WhenNull);

            // `pets.First(p => p is not Bird)`, `pets.Where(...).Single()`: an element that passed the filters.
            case IInvocationOperation invocation when !Functions.IsIntercepted(invocation) && Sequences.ElementResult(invocation) is var (sequence, predicate, mayBeDefault):
                var element = FactsFromFilters(sequence, steps, space, model, session, ct, copies)
                    | (predicate is null ? CaseSet.Empty : FilterFacts(predicate, steps, space, model, session))
                    | FirstSkipped(invocation, sequence, steps, space, model, session);
                return mayBeDefault ? CaseSet.Of(element.Cases) : element;

            case IInvocationOperation invocation:
                return steps.Count == 0 && !Functions.IsIntercepted(invocation) ? FromReturns(invocation.TargetMethod, awaited: false) : CaseSet.Empty;

            // `await MakeCat()` (or `await MakeCat().ConfigureAwait(false)`), for an async method: what its returns are.
            case IAwaitOperation { Operation: var awaitedCall } when steps.Count == 0:
                if (awaitedCall is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: IInvocationOperation configured })
                {
                    awaitedCall = configured;
                }

                return awaitedCall is IInvocationOperation awaitedInvocation && !Functions.IsIntercepted(awaitedInvocation) ? FromReturns(awaitedInvocation.TargetMethod, awaited: true) : CaseSet.Empty;

            // `new Owner(cat).Pet`, `new Owner { Pet = cat }.Pet`, `(owner with { Pet = cat }).Pet`, `owners.First().Pet`
            case IPropertyReferenceOperation or IFieldReferenceOperation when ((IMemberReferenceOperation)expression).Instance is { } instance
                && TrackedValue.From(instance, session) is null
                && TrackedValue.IsStableMember(((IMemberReferenceOperation)expression).Member, model.Compilation, session):
                return KnownExcluded(instance, [.. steps.Prepend(new MemberStep(((IMemberReferenceOperation)expression).Member))], space, model, session, ct, copies);

            case IObjectCreationOperation or IWithOperation when steps.Count > 0 && steps[0] is MemberStep { Member: var member }:
                return OfNewMember(expression, member) is { } set
                    ? KnownExcluded(set, [.. steps.Skip(1)], space, model, session, ct, copies)
                    : expression is IWithOperation { Operand: var original } with && Functions.CopiesAsIs(with) && OnlyStores(with.Initializer.Initializers)
                        && TrackedValue.From(original, session)?.WithMember(member, model.Compilation) is { } unchanged
                        && Extended(unchanged, steps.Skip(1)) is { } rest && IsTrackable(rest, expression.Syntax, model, ct)
                        ? Excluded(expression.Syntax, rest, space, model, ct, onEntry: false, copies + 1)
                        : CaseSet.Empty;
        }

        // A value the analysis follows: what's ruled out for it (and its steps) there.
        if (TrackedValue.From(expression, session) is { } followed)
        {
            var path = Extended(followed, steps);
            if (path is not null && IsTrackable(path, expression.Syntax, model, ct))
            {
                return Excluded(expression.Syntax, path, space, model, ct, onEntry: false, copies + 1);
            }
        }

        return CaseSet.Empty;

        // A value of the operand's static type: not a case none of whose values can be one, and not null if the
        // operand can't be.
        // The object made into a case (`new NotFound(1)` for a union with a closed case type AppError): it's still what it
        // was before it was converted to the case type.
        CaseSet FromCase(IOperation operand)
        {
            while (operand is IConversionOperation { Conversion: { IsImplicit: true, IsReference: true }, OperatorMethod: null } widened)
            {
                operand = widened.Operand;
            }

            return operand.Type is not { } type ? CaseSet.Empty
                : Facts.NotOfType(type, space, model.Compilation)
                    | (type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                        || model.GetTypeInfo(operand.Syntax, ct).Nullability.FlowState == NullableFlowState.NotNull ? CaseSet.Null : CaseSet.Empty
                    );
        }

        // What every return of a method declared in this compilation (that the call can't be dispatched away from) is,
        // each one another walk (and a method can return a call to itself). An awaited call is to an async method, whose
        // returns give what the await does; any other is to one that isn't.
        CaseSet FromReturns(IMethodSymbol method, bool awaited)
        {
            var calleeSpace = space.Reading(method, model.Compilation);
            method = method.OriginalDefinition;
            if (method.IsAsync != awaited || (!session.FollowsCallees && method.MethodKind != MethodKind.LocalFunction) || Functions.OwnDeclaration(method, model.Compilation, ct) is not { } declaration)
            {
                return CaseSet.Empty;
            }

            var calleeModel = Functions.SemanticModelFor(declaration, model);
            CaseSet FromCallee(IOperation returned) =>
                KnownExcluded(returned, steps, calleeSpace, calleeModel, session, ct, copies + 1);

            var (arrow, body) = declaration switch
            {
                MethodDeclarationSyntax m => (m.ExpressionBody, (SyntaxNode?)m.Body),
                LocalFunctionStatementSyntax l => (l.ExpressionBody, l.Body),
                _ => (null, null),
            };

            if (arrow is not null)
            {
                return calleeModel.GetOperation(arrow, ct)?.Descendants().OfType<IReturnOperation>().FirstOrDefault() is { ReturnedValue: { } returned } ? FromCallee(returned) : CaseSet.Empty;
            }

            if (body is null || body.DescendantNodes(n => !Functions.IsFunction(n)).Any(n => n is YieldStatementSyntax))
            {
                return CaseSet.Empty;
            }

            CaseSet? all = null;
            foreach (var statement in body.DescendantNodes(n => !Functions.IsFunction(n)).OfType<ReturnStatementSyntax>())
            {
                if (calleeModel.GetOperation(statement, ct) is not IReturnOperation { ReturnedValue: { } returned })
                {
                    return CaseSet.Empty;
                }

                all = Meet(all, FromCallee(returned));
            }

            return all ?? CaseSet.Empty;
        }

        // What a new object's member was set to: by its initializer (`new Owner { Pet = cat }`, `with { Pet = cat }`),
        // or, for a record's positional property, by the primary constructor's argument for it. Only if what the
        // initializer does after that just stores values, rather than running a setter or init accessor of its own,
        // which could set the member again.
        static IOperation? OfNewMember(IOperation creation, ISymbol member)
        {
            var initializers = creation switch
            {
                IObjectCreationOperation c => c.Initializer?.Initializers ?? [],
                IWithOperation w => w.Initializer.Initializers,
                _ => [],
            };

            for (var i = 0; i < initializers.Length; i++)
            {
                if (initializers[i] is ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } } target } assignment
                    && SymbolEqualityComparer.Default.Equals(target.Member, member)
                )
                {
                    return OnlyStores(initializers.Skip(i + 1)) ? assignment.Value : null;
                }
            }

            // Set later by the initializer, or maybe by code it runs? Then the constructor's value isn't the one that stays.
            if (initializers.Any(i => i is ISimpleAssignmentOperation { Target: IMemberReferenceOperation t } && SymbolEqualityComparer.Default.Equals(t.Member, member)) || !OnlyStores(initializers))
            {
                return null;
            }

            return creation is IObjectCreationOperation { Constructor: { } constructor } created
                && member is IPropertySymbol { ContainingType.IsRecord: true } property
                && property.DeclaringSyntaxReferences.All(r => r.GetSyntax() is ParameterSyntax)
                && constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax) // the primary constructor
                && created.Arguments.FirstOrDefault(a => a.Parameter?.Name == property.Name) is { } argument
                ? argument.Value
                : null;
        }

        // Each of the initializers sets a field or an auto-property, which runs no code of the type's own.
        static bool OnlyStores(IEnumerable<IOperation> initializers) => initializers.All(initializer => initializer is ISimpleAssignmentOperation { Target: var target } && target switch
        {
            IFieldReferenceOperation => true,
            IPropertyReferenceOperation { Property: { IsIndexer: false } property } => Symbols.IsAutoProperty(property),
            _ => false,
        });

        // Only through members: the steps have come apart from the value they were read from, which knew whether it
        // was externally mutable.
        TrackedValue? Extended(TrackedValue start, IEnumerable<Step> more)
        {
            TrackedValue? path = start;
            foreach (var step in more)
            {
                path = step is MemberStep { Member: var member } ? path?.WithMember(member, model.Compilation) : null;
            }

            return path;
        }
    }
}

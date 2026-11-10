using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Works out which cases of a tracked value earlier code has already ruled out at a given point.
/// </summary>
internal static partial class Narrowing
{
    /// <summary>
    /// Everything the walk needs about the value it's following.
    /// </summary>
    /// <param name="start">The node the walk started from.</param>
    /// <param name="onEntry">Whether the walk wants what holds as control enters <paramref name="start"/>.</param>
    /// <param name="usage">How the value's local or parameter root is used, or null if it starts from <c>this</c> or a static member.</param>
    private class Context(TrackedValue value, CaseSpace space, SemanticModel model, SyntaxNode start, bool onEntry, VariableUsage? usage, CancellationToken ct, int copies = 0, LabeledStatementSyntax? joinedLabel = null)
    {
        public CancellationToken CancellationToken =>
            ct;

        /// <summary>
        /// How many copies (or assignments) back the walk this context is for already is.
        /// </summary>
        public int Copies =>
            copies;

        /// <summary>
        /// A label the walk passes as though it weren't there (see <see cref="AtJoinedLabel"/>).
        /// </summary>
        public LabeledStatementSyntax? JoinedLabel =>
            joinedLabel;

        public SemanticModel Model =>
            model;

        public CaseSpace Space =>
            space;

        /// <summary>
        /// The node the walk started from.
        /// </summary>
        public SyntaxNode Start =>
            start;

        /// <summary>
        /// The write the walk stopped at on its way out, if it stopped at one.
        /// </summary>
        public StoppedAtWrite? StoppedAtWrite { get; set; }

        /// <summary>
        /// How the value's local or parameter root is used, or null if it starts from <c>this</c> or a static member.
        /// </summary>
        public VariableUsage? Usage =>
            usage;

        public TrackedValue Value =>
            value;

        /// <summary>
        /// For a local (a <c>bool</c>, or one set to <c>x as T</c>), what it was set to, if testing the local still means testing that:
        /// the value never changes, so what the local was set to was worked out on the value it has now;
        /// and the local only ever gets one value, from its declaration (which runs once), or from the one assignment statement there is to it (which reading it means has run).
        /// </summary>
        private IOperation? Flag(ILocalSymbol local)
        {
            if (value.Variable is not null && usage is not { NeverChanges: true })
            {
                return null;
            }

            if (local.DeclaringSyntaxReferences is not { Length: 1 } references
                || references[0].GetSyntax(ct) is not VariableDeclaratorSyntax declarator
                || declarator.SyntaxTree != start.SyntaxTree
                || value.Session.UsageOf(local, start, model, ct) is not { } flag
            )
            {
                return null;
            }

            if (declarator.Initializer is { Value: var initial })
            {
                return flag.NeverChanges ? model.GetOperation(initial, ct) : null;
            }

            // `bool isBird; ... isBird = x is Bird;`
            return flag.Writes.Count == 1
                && flag.Writes[0] is { Parent: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Parent: ExpressionStatementSyntax } assignment } write
                && assignment.Left == write
                && !WriteAnalysis.Writes(assignment.Right, value, model, ct)
                    ? model.GetOperation(assignment.Right, ct)
                    : null;
        }

        /// <summary>
        /// Whether working out where an assignment stores (the parts of its target evaluated before the right side) writes the value.
        /// Naming a variable, or a member of one, doesn't.
        /// </summary>
        private bool WritesWhileLocating(ExpressionSyntax target) => target switch
        {
            IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or DeclarationExpressionSyntax => false,
            ParenthesizedExpressionSyntax parenthesized => WritesWhileLocating(parenthesized.Expression),
            MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member => WritesWhileLocating(member.Expression),
            ElementAccessExpressionSyntax element =>
                WritesWhileLocating(element.Expression) || element.ArgumentList.Arguments.Any(argument => Writes(argument.Expression)),
            TupleExpressionSyntax tuple => tuple.Arguments.Any(argument => WritesWhileLocating(argument.Expression)),
            _ => Writes(target),
        };

        /// <summary>
        /// For a condition that <see cref="AssignsBeforeTesting"/>, what the value it assigns is known not to be.
        /// </summary>
        public CaseSet AssignedInCondition(ExpressionSyntax condition) =>
            condition.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) && TrackedValue.From(model.GetOperation(a.Left, ct), value.Session)?.Equals(value) == true) is { } assignment
            && model.GetOperation(assignment, ct) is ISimpleAssignmentOperation { Value: var assigned }
            && copies < Limits.Copies
                ? KnownExcluded(assigned, [], space, model, value.Session, ct, copies + 1)
                : CaseSet.Empty;

        /// <summary>
        /// For a condition that writes the value, what's known about it where the condition was true (or false), from the tests it makes after its last write:
        /// in <c>TryGet(out var pet) &amp;&amp; pet is not Bird</c>, the test is of what TryGet set, and holds wherever the whole condition does
        /// (and <c>a || b</c> is only false where both are).
        /// A pattern that binds the whole of what it tests to the variable (<c>Next() is not Bird and var pet</c>) says the same about it.
        /// </summary>
        public CaseSet AfterLastWrite(ExpressionSyntax condition, bool whenTrue)
        {
            condition = WriteAnalysis.WithoutParentheses(condition);
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(whenTrue ? SyntaxKind.LogicalAndExpression : SyntaxKind.LogicalOrExpression))
            {
                if (Writes(binary.Right))
                {
                    return AfterLastWrite(binary.Right, whenTrue);
                }

                var tested = Condition(binary.Right);
                return (whenTrue ? tested.AlwaysFalse : tested.AlwaysTrue) | AfterLastWrite(binary.Left, whenTrue);
            }

            if (condition is IsPatternExpressionSyntax isPattern && BindsWholeInput(isPattern.Pattern) && model.GetOperation(isPattern, ct) is IIsPatternOperation { Pattern: var pattern })
            {
                var outcome = Pattern(pattern);
                return whenTrue ? outcome.AlwaysFalse : outcome.AlwaysTrue;
            }

            return CaseSet.Empty;

            bool BindsWholeInput(PatternSyntax pattern) => pattern switch
            {
                VarPatternSyntax { Designation: SingleVariableDesignationSyntax designation } => Declares(designation),
                DeclarationPatternSyntax { Type.IsVar: true, Designation: SingleVariableDesignationSyntax designation } => Declares(designation),
                ParenthesizedPatternSyntax parenthesized => BindsWholeInput(parenthesized.Pattern),
                BinaryPatternSyntax both when both.IsKind(SyntaxKind.AndPattern) => BindsWholeInput(both.Left) || BindsWholeInput(both.Right),
                _ => false,
            };

            bool Declares(SingleVariableDesignationSyntax designation) =>
                SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(designation, ct), value.Variable);
        }

        /// <summary>
        /// Whether the only write to the value in the condition is a plain assignment to its variable, <c>(x = e)</c>, and nothing in the condition reads it before that:
        /// what the condition tests about x is then about what it was assigned, which it still holds once the condition is worked out.
        /// (Reads in <c>e</c> are of what x held before, but they aren't tests the condition makes.)
        /// </summary>
        public bool AssignsBeforeTesting(ExpressionSyntax condition)
        {
            if (!value.Steps.IsEmpty || value.IsExternallyMutable || value.Variable is not { } variable || variable is not (ILocalSymbol or IParameterSymbol))
            {
                return false;
            }

            var uses = Symbols.References(variable, condition, model, ct).ToList();
            var writes = uses.Where(use => WriteAnalysis.IsWrite(use, model, ct)).ToList();

            return writes.Count == 1
                && writes[0].Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment
                && assignment.Left == writes[0]
                && !uses.Any(use => use.SpanStart < assignment.SpanStart)
                && !condition.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax);
        }

        /// <summary>
        /// Whether the walk can carry on past the boundary of the function it's in.
        /// Only out of a lambda or a query (which run after they're created) inside the function the value belongs to, and only for a value that never changes.
        /// A chain from <c>this</c> or a static member never does (outside construction).
        /// </summary>
        public bool CanWalkOutOf(SyntaxNode function) =>
            function is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax
            && !value.IsExternallyMutable
            && !value.IndexVariables.Any()
            && !(value.Variable is null && !value.Steps.IsEmpty && (Functions.IsInConstruction(function) || Functions.MayBeCreatedDuringConstruction(function, model, ct))) // a constructor can assign it later
            && !value.MembersReadThrough.Any(member => member is { IsStatic: false, ContainingType: { IsReferenceType: true } owner } && Functions.ThisEscapesDuringConstruction(owner, model, ct))
            && (value.Variable is null || (usage is { NeverChanges: true } or { SetForGoodWhereCaptured: true } && function != usage.DeclaringFunction));

        /// <summary>
        /// What a switch statement's case label on <paramref name="input"/> says about the value.
        /// </summary>
        public Outcome ClauseOn(IOperation input, ICaseClauseOperation clause) => clause switch
        {
            IPatternCaseClauseOperation patternClause => PatternOn(input, patternClause.Pattern),
            _ when value.IsReadBy(input) => Facts.Of(clause, space, model.Compilation),
            ISingleValueCaseClauseOperation single => Facts.OfConstantOnDiscriminant(input, single, value, space, model.Compilation) ?? Outcome.Unknown,
            _ => Outcome.Unknown, // a `default:` label is tried last, wherever it is
        };

        public Outcome Condition(ExpressionSyntax condition) =>
            model.GetOperation(WriteAnalysis.WithoutParentheses(condition), ct) is { } operation ? Condition(operation) : Outcome.Unknown;

        public Outcome Condition(IOperation condition) =>
            Facts.Of(condition, value, space, model.Compilation, Flag);

        /// <summary>
        /// What's ruled out by an arm (or case label) on <paramref name="input"/> not matching:
        /// the cases its pattern always matches, and, with a <c>when</c> clause, only those for which the clause is always true too (<c>_ when x is Dog</c>).
        /// </summary>
        public CaseSet DidNotMatch(IOperation input, IPatternOperation pattern, IOperation? guard) =>
            guard is null
                ? PatternOn(input, pattern).AlwaysTrue
                : PatternOn(input, pattern).AlwaysTrue & GuardOn(input, pattern, guard).AlwaysTrue;

        /// <summary>
        /// Whether control can never fall out of the end of the statement:
        /// it always returns, throws, jumps elsewhere, or calls a [DoesNotReturn] method.
        /// Errs towards no.
        /// </summary>
        public bool Exits(StatementSyntax statement) => statement switch
        {
            _ when model.AnalyzeControlFlow(statement) is { Succeeded: true, EndPointIsReachable: false } => true,
            ExpressionStatementSyntax expression => model.GetOperation(expression.Expression, ct) is IInvocationOperation { TargetMethod: var method } && Functions.NeverReturns(method),
            BlockSyntax block => block.Statements.Count > 0 && Exits(block.Statements[block.Statements.Count - 1]),
            LabeledStatementSyntax labeled => Exits(labeled.Statement),
            IfStatementSyntax { Else: { } elseClause } ifStatement => Exits(ifStatement.Statement) && Exits(elseClause.Statement),
            _ => false,
        };

        /// <summary>
        /// Whether patterns on <paramref name="input"/> say something about the value:
        /// it's the value, or what the value is a member of.
        /// </summary>
        public bool Follows(IOperation input) =>
            value.IsReadBy(input)
            || (TrackedValue.From(input, value.Session) is { } owner && owner.IsPrefixOf(value))
            || (input is ITupleOperation tuple && tuple.Elements.Any(Follows))
            || Facts.ReadsDiscriminant(input, value, space, model.Compilation);

        /// <summary>
        /// What an arm's (or label's) <c>when</c> clause says about the value, also through what its pattern binds the input to (see <see cref="Facts.OfGuard"/>).
        /// </summary>
        public Outcome GuardOn(IOperation input, IPatternOperation pattern, IOperation guard) =>
            Facts.OfGuard(input, pattern, guard, value, space, model, Flag);

        /// <summary>
        /// Whether the only thing in the statement that writes the variable is its declaration.
        /// </summary>
        public bool OnlyDeclares(StatementSyntax statement) =>
            usage is not null
            && usage.Declarations.Any(d => statement.Span.Contains(d.Span))
            && !usage.Writes.Any(w => statement.Span.Contains(w.Span));

        public Outcome Pattern(IPatternOperation pattern) =>
            Facts.Of(pattern, space, model.Compilation);

        /// <summary>
        /// What a pattern on <paramref name="input"/> (the value, what it's a member of, or a tuple literal with one of those in it) says about the value.
        /// </summary>
        public Outcome PatternOn(IOperation input, IPatternOperation pattern)
        {
            if (input is ITupleOperation tuple && !value.IsReadBy(input))
            {
                for (var i = 0; i < tuple.Elements.Length; i++)
                {
                    var element = tuple.Elements[i];
                    if (Follows(element))
                    {
                        return Facts.OfTuplePattern(pattern, i, tuple.Elements.Length, space, sub => PatternOn(element, sub));
                    }
                }
            }

            return Facts.OfPatternOn(input, pattern, value, space, model.Compilation) ?? Outcome.Unknown;
        }

        /// <summary>
        /// Whether the statement or expression writes the value anywhere.
        /// Errs towards yes.
        /// </summary>
        public bool Writes(SyntaxNode node) =>
            WriteAnalysis.Writes(node, value, model, ct);

        /// <summary>
        /// Whether <paramref name="child"/>, which contains the node the walk started from, can write the value before control gets to that node (including by looping back around to it).
        /// Errs towards yes, except for an assignment with that node on its right (<c>x = x switch { ... }</c>, <c>x ??= ...</c>, <c>(x, y) = (x switch { ... }, 1)</c>):
        /// C# works out where to store, then evaluates the right side, and only then stores.
        /// </summary>
        public bool WritesBefore(SyntaxNode child)
        {
            if (onEntry && child == start)
            {
                return false;
            }

            var expression = child is ExpressionStatementSyntax statement ? statement.Expression : child as ExpressionSyntax;
            if (expression is AssignmentExpressionSyntax assignment && assignment.Right.Span.Contains(start.Span)
                && !start.Ancestors().TakeWhile(a => a != assignment).Any(Functions.IsFunction)
            )
            {
                return WritesWhileLocating(assignment.Left) || Writes(assignment.Right);
            }

            // A switch statement's sections only run once it has matched, and control never comes back around to it
            // from them (a loop or goto around it is a level further out).
            if (child == start && start is SwitchStatementSyntax switchStatement)
            {
                return Writes(switchStatement.Expression);
            }

            // For a value something else could change, what runs after it's read doesn't matter, unless control can come
            // back around to it (a loop, or a goto).
            if (value.IsExternallyMutable
                && child.Span.Contains(start.Span)
                && child is not (WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax)
                && !start.Ancestors().TakeWhile(a => a != child).Any(a => a is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax)
                && !child.DescendantNodes().OfType<GotoStatementSyntax>().Any()
            )
            {
                return WriteAnalysis.Writes(child, value, model, before: start.SpanStart, ct);
            }

            return Writes(child);
        }
    }

    /// <summary>
    /// Where the walk stopped at a write:
    /// <c>Statements[WriteIndex]</c>, scanning back from <c>Statements[ChildIndex]</c> (which holds where the walk started).
    /// </summary>
    private class StoppedAtWrite(IReadOnlyList<StatementSyntax> statements, int writeIndex, int childIndex)
    {
        public int ChildIndex =>
            childIndex;

        public IReadOnlyList<StatementSyntax> Statements =>
            statements;

        public StatementSyntax Write =>
            statements[writeIndex];

        public int WriteIndex =>
            writeIndex;
    }

    private static bool AddArm(SwitchExpressionArmSyntax arm, SyntaxNode child, Context context, ref CaseSet excluded)
    {
        // `when` clauses run while matching: every one up to this arm's, and this arm's own unless it's `child`.
        var outer = (SwitchExpressionSyntax)arm.Parent!;
        var index = outer.Arms.IndexOf(arm);

        if (context.WritesBefore(child)|| outer.Arms.Take(index + 1).Any(a => a.WhenClause is { } when && when != child && context.Writes(when.Condition)))
        {
            return false;
        }

        if (context.Model.GetOperation(outer, context.CancellationToken) is not ISwitchExpressionOperation outerOp)
        {
            return true;
        }

        // In the arm's value, its `when` clause held, whatever the switch is over.
        var own = outerOp.Arms[index];
        if (own.Guard is { } ownGuard && child != arm.WhenClause)
        {
            excluded |= context.GuardOn(outerOp.Value, own.Pattern, ownGuard).AlwaysFalse;
        }

        if (!context.Follows(outerOp.Value))
        {
            // An earlier arm whose pattern matches anything didn't match because its `when` clause didn't hold.
            foreach (var earlier in outerOp.Arms.Take(index))
            {
                if (earlier.Guard is { } guard && Facts.MatchesAnything(earlier.Pattern))
                {
                    excluded |= context.GuardOn(outerOp.Value, earlier.Pattern, guard).AlwaysTrue;
                }
            }

            return true;
        }

        excluded |= context.PatternOn(outerOp.Value, own.Pattern).AlwaysFalse;
        foreach (var earlier in outerOp.Arms.Take(index))
        {
            excluded |= context.DidNotMatch(outerOp.Value, earlier.Pattern, earlier.Guard);
        }

        return true;
    }

    private static bool AddBranch(ExpressionSyntax condition, bool whenTrue, SyntaxNode child, Context context, ref CaseSet excluded)
    {
        if (context.WritesBefore(child))
        {
            return false;
        }

        // `if ((pet = Next()) is not Bird) { ... }`: what's tested is what it was just assigned, and nothing from before
        // the condition holds.
        if (context.Writes(condition))
        {
            if (context.AssignsBeforeTesting(condition))
            {
                var assigning = context.Condition(condition);
                excluded |= (whenTrue ? assigning.AlwaysFalse : assigning.AlwaysTrue) | context.AssignedInCondition(condition);
            }
            else
            {
                excluded |= context.AfterLastWrite(condition, whenTrue);
            }

            return false;
        }

        var outcome = context.Condition(condition);
        excluded |= whenTrue ? outcome.AlwaysFalse : outcome.AlwaysTrue;
        return true;
    }

    /// <summary>
    /// A do loop's body runs once before the condition is checked:
    /// the first time through, what held on the way into the loop holds;
    /// after that, the condition was true.
    /// Only what holds both ways is ruled out.
    /// </summary>
    /// <remarks>Always returns false:
    /// what holds further out, on the way into the loop, is already in what's added (and doesn't hold on later times through).</remarks>
    private static bool AddDoBody(DoStatementSyntax loop, SyntaxNode child, Context context, ref CaseSet excluded)
    {
        if (context.Writes(loop.Condition) || (child is not BlockSyntax && context.WritesBefore(child)))
        {
            return false;
        }

        var onEntry = Excluded(loop, context.Value, context.Space, context.Model, context.CancellationToken, onEntry: true, context.Copies + 1);
        excluded |= context.Condition(loop.Condition).AlwaysFalse & onEntry;
        return false;
    }

    /// <remarks>Unlike a branch, the body may write the value after <paramref name="child"/>'s part in it:
    /// the condition is checked again before the next iteration.
    /// A block body was already checked from its start up to that part, by the level below (a statement list).</remarks>
    private static bool AddLoopBody(ExpressionSyntax condition, SyntaxNode child, Context context, ref CaseSet excluded)
    {
        if (child is not BlockSyntax && context.WritesBefore(child))
        {
            return false;
        }

        // `while ((pet = Next()) is not Bird) { ... }`: what's tested is what it was just assigned.
        if (context.Writes(condition))
        {
            excluded |= context.AssignsBeforeTesting(condition)
                ? context.Condition(condition).AlwaysFalse | context.AssignedInCondition(condition)
                : context.AfterLastWrite(condition, whenTrue: true);

            return false;
        }

        excluded |= context.Condition(condition).AlwaysFalse;

        return true;
    }

    /// <summary>
    /// Adds what <paramref name="parent"/> establishes on the way into <paramref name="child"/>.
    /// Returns false if something between there and <paramref name="child"/>'s end could write the value, in which case nothing established further out can be trusted either.
    /// </summary>
    /// <remarks>Each level that establishes something checks that its <paramref name="child"/> doesn't write the value.
    /// Levels that establish nothing don't need to:
    /// the next level out that does checks its own child, which contains this one.</remarks>
    private static bool AddWhatParentEstablishes(SyntaxNode parent, SyntaxNode child, Context context, ref CaseSet excluded)
    {
        switch (parent)
        {
            // In a statement list, what the earlier statements established.
            case BlockSyntax or GlobalStatementSyntax or SwitchSectionSyntax when child is StatementSyntax statement:
                var statements = Statements.StatementsAround(parent)!;
                var index = Statements.IndexOf(statements, statement);
                if (context.WritesBefore(statement))
                {
                    return false;
                }

                if (statement is LabeledStatementSyntax labeled && labeled != context.JoinedLabel && !OnlyJumpedBackTo(labeled, statements, index, context))
                {
                    excluded |= AtJoinedLabel(labeled, context) ?? CaseSet.Empty;
                    return false;
                }

                if (!ScanBackward(statements, index, context, ref excluded, onPath: true))
                {
                    return false;
                }

                if (parent is SwitchSectionSyntax section)
                {
                    excluded |= SectionFacts(section, context);
                }

                return true;

            // In code that runs only when a condition was true (or false), what that implies.
            case IfStatementSyntax ifStatement when child == ifStatement.Statement:
                return AddBranch(ifStatement.Condition, whenTrue: true, child, context, ref excluded);
            
            case ElseClauseSyntax { Parent: IfStatementSyntax ifStatement }:
                return AddBranch(ifStatement.Condition, whenTrue: false, child, context, ref excluded);
            
            case ConditionalExpressionSyntax conditional when child == conditional.WhenTrue || child == conditional.WhenFalse:
                return AddBranch(conditional.Condition, whenTrue: child == conditional.WhenTrue, child, context, ref excluded);
            
            case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalAndExpression):
                return AddBranch(binary.Left, whenTrue: true, child, context, ref excluded);

            case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalOrExpression):
                return AddBranch(binary.Left, whenTrue: false, child, context, ref excluded);

            // In a loop body, that the condition was true when this iteration started.
            case WhileStatementSyntax loop when child == loop.Statement:
                return AddLoopBody(loop.Condition, child, context, ref excluded);
            
            case ForStatementSyntax { Condition: { } condition } loop when child == loop.Statement:
                return AddLoopBody(condition, child, context, ref excluded);
            
            case DoStatementSyntax loop when child == loop.Statement:
                return AddDoBody(loop, child, context, ref excluded);

            // In a foreach over a LINQ chain, what its Where filters say about the elements that get through. (Not for
            // what only something else could change, which could change after the filter.)
            case ForEachStatementSyntax { AwaitKeyword.RawKind: 0 } loop when child == loop.Statement
                && !context.Value.IsExternallyMutable
                && context.Value.Variable is ILocalSymbol { IsForEach: true } iteration
                && SymbolEqualityComparer.Default.Equals(context.Model.GetDeclaredSymbol(loop, context.CancellationToken), iteration)
                && context.Model.GetOperation(loop.Expression, context.CancellationToken) is { } collection
                && SymbolEqualityComparer.Default.Equals(Sequences.ElementType(collection), iteration.Type):
                excluded |= FactsFromFilters(collection, context.Value.Steps, context.Space, context.Model, context.Value.Session, context.CancellationToken, context.Copies);
                return true;

            // `foreach (var (pet, n) in items.Where(...))`: the variable is a member of each element.
            case ForEachVariableStatementSyntax { AwaitKeyword.RawKind: 0 } loop when child == loop.Statement
                && !context.Value.IsExternallyMutable
                && context.Value.Variable is ILocalSymbol deconstructed
                && context.Model.GetOperation(loop, context.CancellationToken) is IForEachLoopOperation { LoopControlVariable: { } variable, Collection: var collection }
                && PathTo(variable, deconstructed) is { Count: > 0 } path
                && MembersAlong(path, Sequences.ElementType(collection), context.Model.GetDeconstructionInfo(loop), context.Value.Session) is { } elementMembers:
                excluded |= FactsFromFilters(collection, [.. elementMembers, .. context.Value.Steps], context.Space, context.Model, context.Value.Session, context.CancellationToken, context.Copies);
                return true;

            // In a switch expression arm, that its pattern matched and earlier arms' didn't.
            case SwitchExpressionArmSyntax arm:
                return AddArm(arm, child, context, ref excluded);

            // In a catch with a filter, that the filter held. Filters run before the finally blocks in the try (that's
            // how a handler is picked), so they only say something if nothing in the try block, or in a filter, writes
            // the value.
            case CatchClauseSyntax { Filter: { } filter, Parent: TryStatementSyntax tryStatement } catchClause when child == catchClause.Block:
                if (!context.Value.IsExternallyMutable && !context.Writes(tryStatement.Block) && !tryStatement.Catches.Any(c => c.Filter is { } each && context.Writes(each.FilterExpression)))
                {
                    excluded |= context.Condition(filter.FilterExpression).AlwaysFalse;
                }

                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// What holds at every <c>break</c> that ends <paramref name="target"/> (a loop or switch statement), each worked out from where it is, or null if there's no such break.
    /// A break with a <c>finally</c> on the way out that writes the value holds nothing, since the finally runs after it.
    /// </summary>
    private static CaseSet? AtBreaks(StatementSyntax target, Context context)
    {
        CaseSet? atBreaks = null;
        foreach (var breakStatement in Statements.BreaksEnding(target, target))
        {
            var finallyWrites = breakStatement.Ancestors().TakeWhile(a => a != target).OfType<TryStatementSyntax>()
                .Any(t => t.Finally is { } finallyClause && !finallyClause.Span.Contains(breakStatement.Span) && context.Writes(finallyClause.Block));

            var atBreak = context.Copies < Limits.Copies && !context.Value.IsExternallyMutable && !finallyWrites && IsTrackable(context.Value, breakStatement, context.Model, context.CancellationToken)
                ? Excluded(breakStatement, context.Value, context.Space, context.Model, context.CancellationToken, onEntry: false, context.Copies + 1)
                : CaseSet.Empty;

            atBreaks = Meet(atBreaks, atBreak);
        }

        return atBreaks;
    }

    /// <summary>
    /// What holds at a label that control gets to by falling into it or through gotos from earlier on:
    /// what holds on every one of those ways in.
    /// Null if some goto to it comes from later on (see <see cref="OnlyJumpedBackTo"/> for those), or leaves a try block with a finally on the way, which could write the value.
    /// </summary>
    private static CaseSet? AtJoinedLabel(LabeledStatementSyntax label, Context context)
    {
        if (context.Copies >= Limits.Copies || Functions.EnclosingFunction(label) is not { } function)
        {
            return null;
        }

        if (label.Parent is SwitchSectionSyntax { Parent: SwitchStatementSyntax switchStatement }
            && Statements.GotosIntoSections(switchStatement)
        )
        {
            return null;
        }

        var jumps = function.DescendantNodes(n => n == function || !Functions.IsFunction(n))
            .OfType<GotoStatementSyntax>()
            .Where(jump => jump.IsKind(SyntaxKind.GotoStatement) && jump.Expression is IdentifierNameSyntax target && target.Identifier.ValueText == label.Identifier.ValueText)
            .ToList();

        if (jumps.Count == 0
            || jumps.Any(jump => jump.SpanStart > label.SpanStart
                || jump.Ancestors().TakeWhile(a => !a.Span.Contains(label.Span)).OfType<TryStatementSyntax>().Any(t => t.Finally is not null)
            )
        )
        {
            return null;
        }

        // Control can only fall into the label if the statement before it can complete (`return 0; done: ...` can't).
        var (value, space, model, ct) = (context.Value, context.Space, context.Model, context.CancellationToken);
        var statements = Statements.StatementsAround(label.Parent!);
        var index = statements is null ? -1 : Statements.IndexOf(statements, label);
        CaseSet? atLabel = index > 0 && context.Exits(statements![index - 1])
            ? null
            : Excluded(label, value, space, model, ct, onEntry: true, context.Copies + 1, joinedLabel: label);

        foreach (var jump in jumps)
        {
            atLabel = Meet(atLabel, IsTrackable(value, jump, model, ct) ? Excluded(jump, value, space, model, ct, onEntry: false, context.Copies + 1) : CaseSet.Empty);
        }

        return atLabel;
    }

    /// <param name="onEntry">Whether what's wanted is what holds as control enters <paramref name="node"/>, rather than at it:
    /// writes inside <paramref name="node"/> itself don't matter then.</param>
    /// <param name="copies">How many copies back this walk already is.</param>
    /// <param name="joinedLabel">A label to walk past as though it weren't there:
    /// the walk is working out what holds when control falls into it (see <see cref="AtJoinedLabel"/>).</param>
    private static CaseSet Excluded(SyntaxNode node, TrackedValue value, CaseSpace space, SemanticModel model, CancellationToken ct, bool onEntry, int copies, LabeledStatementSyntax? joinedLabel = null)
    {
        var excluded = CaseSet.Empty;
        var usage = value.Variable is null ? null : value.Session.UsageOf(value.Variable, node, model, ct);
        var context = new Context(value, space, model, node, onEntry, usage, ct, copies, joinedLabel);
        var child = node;
        SyntaxNode? reachedFunction = null;

        for (var parent = node.Parent; parent is not null; child = parent, parent = parent.Parent)
        {
            if (Functions.IsFunction(parent) && !context.CanWalkOutOf(parent))
            {
                reachedFunction = parent;
                break;
            }

            if (!AddWhatParentEstablishes(parent, child, context, ref excluded))
            {
                break;
            }
        }

        if (copies < Limits.Copies)
        {
            if (context.StoppedAtWrite is { } stop)
            {
                excluded |= FactsFromCopy(stop, context);
            }

            if (usage is not null)
            {
                excluded |= FactsFromDesignation(usage, context) | FactsFromEveryWrite(usage, context);
            }

            // Made it all the way to the start of the function the parameter belongs to: what's known about the
            // arguments it can get.
            if (usage is not null && reachedFunction is not null && reachedFunction == usage.DeclaringFunction)
            {
                excluded |= FactsOnEntry(context);
            }

            // To the start of a local function that captures the variable: what holds wherever it's called.
            if (usage is not null && reachedFunction is LocalFunctionStatementSyntax local && reachedFunction != usage.DeclaringFunction)
            {
                excluded |= FactsFromCallsOf(local, context);
            }

            // To the start of a member that reads a class's primary constructor parameter, which nothing assigns: it holds
            // what the constructor was called with.
            if (usage is { NeverChanges: true, DeclaringFunction: TypeDeclarationSyntax } && reachedFunction is not null && reachedFunction != usage.DeclaringFunction)
            {
                excluded |= FactsOnEntry(context);
            }

            // A member that only ever holds its initializer's value.
            excluded |= FactsFromInitializers(value, space, model, ct, copies);
        }

        return excluded;
    }

    /// <summary>
    /// What's ruled out once control falls out of the end of <paramref name="statement"/>, which doesn't write the value.
    /// </summary>
    private static CaseSet FactsAtEnd(StatementSyntax statement, Context context)
    {
        switch (statement)
        {
            // The end of an `if` is reached through each branch that can fall through, with what that branch implies
            // about the condition. `if (x is T) <exit>` is the simplest case: only the empty else falls through.
            case IfStatementSyntax ifStatement:
                var condition = context.Condition(ifStatement.Condition);
                var ifTrue = context.Exits(ifStatement.Statement)
                    ? null
                    : condition.AlwaysFalse | FactsAtEnd(ifStatement.Statement, context);
                var ifFalse = ifStatement.Else is null
                    ? condition.AlwaysTrue
                    : context.Exits(ifStatement.Else.Statement)
                        ? null
                        : condition.AlwaysTrue | FactsAtEnd(ifStatement.Else.Statement, context);

                return Meet(ifTrue, ifFalse) ?? CaseSet.Empty;

            case BlockSyntax block:
                var atEnd = CaseSet.Empty;

                ScanBackward(block.Statements, block.Statements.Count, context, ref atEnd);

                return atEnd;

            case LabeledStatementSyntax labeled:
                return FactsAtEnd(labeled.Statement, context);

            // Statements whose end is the end of their body.
            case CheckedStatementSyntax checkedStatement:
                return FactsAtEnd(checkedStatement.Block, context);

            case UnsafeStatementSyntax unsafeStatement:
                return FactsAtEnd(unsafeStatement.Block, context);

            case LockStatementSyntax lockStatement:
                return FactsAtEnd(lockStatement.Statement, context);

            case UsingStatementSyntax usingStatement:
                return FactsAtEnd(usingStatement.Statement, context);

            case FixedStatementSyntax fixedStatement:
                return FactsAtEnd(fixedStatement.Statement, context);

            // The end of a try is reached through the end of the try block or of any catch, and then the finally.
            // (A catch can start before the try block has established anything.)
            case TryStatementSyntax tryStatement:
                var throughBody = context.Exits(tryStatement.Block) ? null : FactsAtEnd(tryStatement.Block, context);

                foreach (var catchClause in tryStatement.Catches)
                {
                    throughBody = Meet(throughBody, context.Exits(catchClause.Block) ? null : FactsAtEnd(catchClause.Block, context));
                }

                return (throughBody ?? CaseSet.Empty) | (tryStatement.Finally is { } finallyClause ? FactsAtEnd(finallyClause.Block, context) : CaseSet.Empty);

            case WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax:
                return FactsAtEndOfLoop(statement, context);

            case SwitchStatementSyntax switchStatement:
                return FactsAtEndOfSwitch(switchStatement, context);

            // A statement that's sure to evaluate a throwing expression for some cases (`var n = x switch { Bird =>
            // throw ..., ... };`, `_ = x is Bird ? throw ... : 0;`) only gets to its end for the others.
            case ExpressionStatementSyntax or LocalDeclarationStatementSyntax or YieldStatementSyntax:
                return UnconditionallyEvaluated(statement, context.Model, context.CancellationToken)
                    .Aggregate(CaseSet.Empty, (excluded, expression) => excluded | ThrowsFor(expression, context));

            default:
                return CaseSet.Empty;
        }
    }

    /// <summary>
    /// What's ruled out once a loop ends:
    /// its condition was false (unless it's <c>true</c>, or there's none), or a <c>break</c> ended it, with what held there.
    /// Holds even if the loop writes the value, provided the condition itself doesn't (or only assigns it before testing it).
    /// </summary>
    private static CaseSet FactsAtEndOfLoop(StatementSyntax statement, Context context)
    {
        while (statement is LabeledStatementSyntax labeled)
        {
            statement = labeled.Statement;
        }

        if (statement is not (WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax))
        {
            return CaseSet.Empty;
        }

        var condition = statement switch
        {
            WhileStatementSyntax loop => loop.Condition,
            DoStatementSyntax loop => loop.Condition,
            _ => ((ForStatementSyntax)statement).Condition,
        };

        CaseSet? whenFalse = condition is null || context.Model.GetConstantValue(condition, context.CancellationToken) is { HasValue: true, Value: true }
            ? null
            : context.Writes(condition) && !context.AssignsBeforeTesting(condition)
                ? CaseSet.Empty
                : context.Condition(condition).AlwaysTrue;

        return Meet(whenFalse, AtBreaks(statement, context)) ?? CaseSet.Empty;
    }

    /// <summary>
    /// What's ruled out once control falls out of the end of a switch statement over the value:
    /// it got there through a <c>break</c> out of a section, which only runs once one of that section's labels matched, or (without a default) because no label matched.
    /// </summary>
    private static CaseSet FactsAtEndOfSwitch(SwitchStatementSyntax switchStatement, Context context)
    {
        if (context.Model.GetOperation(switchStatement, context.CancellationToken) is not ISwitchOperation switchOp || !context.Follows(switchOp.Value))
        {
            return CaseSet.Empty;
        }

        CaseSet? atEnd = null;
        foreach (var section in switchStatement.Sections)
        {
            if (Statements.BreaksOut(section, switchStatement))
            {
                atEnd = Meet(atEnd, SectionFacts(section, context));
            }
        }

        if (!Statements.HasDefault(switchOp))
        {
            atEnd = Meet(atEnd, NoneMatched(switchOp, context));
        }

        return atEnd ?? CaseSet.Empty;
    }

    /// <summary>
    /// Inside a constructor's own body, the readonly members of what it constructs (from <c>this</c>, or static ones in a static constructor) can only be written by that body, where it's visible:
    /// a chain from one of them can be followed there, as long as nothing in the constructor takes a reference to one of the chain's members.
    /// </summary>
    private static bool FollowableInConstructor(TrackedValue value, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (value.Variable is not null
            || value.IsExternallyMutable
            || node.Ancestors().FirstOrDefault(a => a is ConstructorDeclarationSyntax or AccessorDeclarationSyntax or EqualsValueClauseSyntax or PrimaryConstructorBaseTypeSyntax) is not ConstructorDeclarationSyntax constructor
            || model.GetDeclaredSymbol(constructor, ct) is not IMethodSymbol { ContainingType: var type } constructed
            || value.Steps[0] is not MemberStep { Member: var first }
            || !SymbolEqualityComparer.Default.Equals(first.ContainingType, type)
            || first.IsStatic != constructed.IsStatic
        )
        {
            return false;
        }

        var members = value.MembersReadThrough.Concat(value.IndexVariables).Select(m => m.OriginalDefinition).ToList();
        return !constructor.DescendantNodes().Any(n =>
            (n is RefExpressionSyntax || n is ArgumentSyntax argument && argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || n is PrefixUnaryExpressionSyntax prefix && prefix.IsKind(SyntaxKind.AddressOfExpression)
            )
            && n.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => model.GetSymbolInfo(name, ct).Symbol is { } symbol
                && members.Contains(symbol.OriginalDefinition, SymbolEqualityComparer.Default)
            )
        );
    }

    /// <summary>
    /// What holds on every path, where null is a path that can't happen.
    /// </summary>
    private static CaseSet? Meet(CaseSet? left, CaseSet? right) =>
        left is null ? right : right is null ? left : left & right;

    /// <summary>
    /// What's ruled out when no label of a switch statement matched:
    /// for one over the value, what each label is sure to match.
    /// </summary>
    private static CaseSet NoneMatched(ISwitchOperation switchOp, Context context) =>
        !context.Follows(switchOp.Value)
            ? CaseSet.Empty
            : switchOp.Cases.SelectMany(c => c.Clauses).Aggregate(CaseSet.Empty, (excluded, clause) => excluded | (clause is IPatternCaseClauseOperation { Guard: { } guard } guarded
                ? context.DidNotMatch(switchOp.Value, guarded.Pattern, guard)
                : context.ClauseOn(switchOp.Value, clause).AlwaysTrue
        ));

    /// <summary>
    /// Whether control only gets to the label by falling into it or by jumping back to it from later on, without the value changing on the way round:
    /// every goto to it (or to a later label in its list, which would skip what's before it) is after it, and nothing from it on writes the value.
    /// Then what holds before it holds at it.
    /// </summary>
    private static bool OnlyJumpedBackTo(LabeledStatementSyntax label, IReadOnlyList<StatementSyntax> statements, int index, Context context)
    {
        if (label.Parent is SwitchSectionSyntax { Parent: SwitchStatementSyntax switchStatement } && Statements.GotosIntoSections(switchStatement))
        {
            return false;
        }

        var fromHereOn = statements.Skip(index).ToList();
        var labels = fromHereOn.OfType<LabeledStatementSyntax>().Select(l => l.Identifier.ValueText).ToList();
        if (Functions.EnclosingFunction(label) is not { } function
            || function.DescendantNodes(n => n == function || !Functions.IsFunction(n))
                .OfType<GotoStatementSyntax>()
                .Any(jump => jump.IsKind(SyntaxKind.GotoStatement)
                    && jump.Expression is IdentifierNameSyntax target
                    && labels.Contains(target.Identifier.ValueText)
                    && jump.SpanStart < label.SpanStart
                )
        )
        {
            return false;
        }

        return !fromHereOn.Any(context.Writes);
    }

    /// <summary>
    /// Adds what the statements before <paramref name="index"/> establish, nearest first.
    /// Returns false if it had to stop early (at a write or a label), in which case nothing further out holds either.
    /// </summary>
    /// <param name="onPath">Whether the statement at <paramref name="index"/> is on the way to where the walk started (rather than the end of a block before it).
    /// A write it stops at there is recorded, in case it's a copy.</param>
    private static bool ScanBackward(IReadOnlyList<StatementSyntax> statements, int index, Context context, ref CaseSet excluded, bool onPath = false)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var previous = statements[i];
            if (context.Writes(previous))
            {
                if (onPath)
                {
                    context.StoppedAtWrite = new StoppedAtWrite(statements, i, index);
                }

                // A loop's condition is the last thing it evaluates, so what it says still holds after a loop whose
                // body writes the value. And a statement that declares the variable (`if (o is not Pet pet || pet is
                // Bird) return;`) establishes facts after declaring it. Nothing from before either does.
                excluded |= FactsAtEndOfLoop(previous, context);
                if (context.OnlyDeclares(previous))
                {
                    excluded |= FactsAtEnd(previous, context);
                }

                // `if ((pet = Next()) is Bird) return;`: what's tested is what it was just assigned.
                if (previous is IfStatementSyntax assigningIf && context.AssignsBeforeTesting(assigningIf.Condition)
                    && !context.Writes(assigningIf.Statement) && (assigningIf.Else is null || !context.Writes(assigningIf.Else.Statement))
                )
                {
                    excluded |= FactsAtEnd(previous, context) | context.AssignedInCondition(assigningIf.Condition);
                }

                // What the statement leaves in the value: `pet = new Cat(...);`, or `if (pet is Bird) pet = ...;`.
                if (context.Copies < Limits.Copies)
                {
                    excluded |= FactsAtEndOfWrite(previous, context);
                }

                return false;
            }

            excluded |= FactsAtEnd(previous, context);

            if (previous is LabeledStatementSyntax labeled && labeled != context.JoinedLabel && !OnlyJumpedBackTo(labeled, statements, i, context))
            {
                excluded |= AtJoinedLabel(labeled, context) ?? CaseSet.Empty;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// In a section of a switch statement over the value, that one of its labels matched, and that no label tried before it did (labels in textual order, the default last).
    /// </summary>
    private static CaseSet SectionFacts(SwitchSectionSyntax section, Context context)
    {
        var switchStatement = (SwitchStatementSyntax)section.Parent!;
        if (context.Model.GetOperation(switchStatement, context.CancellationToken) is not ISwitchOperation switchOp)
        {
            return CaseSet.Empty;
        }

        // `goto case` and `goto default` enter a section without its labels matching, and `when` clauses run while
        // matching.
        if (Statements.GotosIntoSections(switchStatement) || switchStatement.Sections.SelectMany(s => s.Labels).OfType<CasePatternSwitchLabelSyntax>().Any(label => label.WhenClause is { } when && context.Writes(when.Condition)))
        {
            return CaseSet.Empty;
        }

        var index = switchStatement.Sections.IndexOf(section);
        var clauses = switchOp.Cases[index].Clauses;
        var isDefault = clauses.Any(c => c is IDefaultCaseClauseOperation);
        var onlyDefault = clauses.All(c => c is IDefaultCaseClauseOperation);
        var follows = context.Follows(switchOp.Value);
        var excluded = CaseSet.Empty;

        // One of the section's labels matched, with its `when` clause holding (whatever the switch is over).
        if (!isDefault)
        {
            excluded |= clauses.Select(c => (follows ? context.ClauseOn(switchOp.Value, c).AlwaysFalse : CaseSet.Empty)
                    | (c is IPatternCaseClauseOperation { Guard: { } guard } guarded ? context.GuardOn(switchOp.Value, guarded.Pattern, guard).AlwaysFalse : CaseSet.Empty)
            )
                .Aggregate((left, right) => left & right);
        }

        if (!follows)
        {
            // A label tried earlier whose pattern matches anything didn't match because its `when` clause didn't hold.
            for (var i = 0; i < switchOp.Cases.Length; i++)
            {
                if (i < index || (onlyDefault && i != index))
                {
                    foreach (var clause in switchOp.Cases[i].Clauses)
                    {
                        if (clause is IPatternCaseClauseOperation { Guard: { } guard } guarded && Facts.MatchesAnything(guarded.Pattern))
                        {
                            excluded |= context.GuardOn(switchOp.Value, guarded.Pattern, guard).AlwaysTrue;
                        }
                    }
                }
            }

            return excluded;
        }

        for (var i = 0; i < switchOp.Cases.Length; i++)
        {
            if (i < index || (onlyDefault && i != index))
            {
                foreach (var clause in switchOp.Cases[i].Clauses)
                {
                    excluded |= clause is IPatternCaseClauseOperation { Guard: { } guard } guarded
                        ? context.DidNotMatch(switchOp.Value, guarded.Pattern, guard)
                        : context.ClauseOn(switchOp.Value, clause).AlwaysTrue;
                }
            }
        }

        return excluded;
    }

    /// <summary>
    /// The cases for which evaluating the expression always throws.
    /// </summary>
    private static CaseSet ThrowsFor(ExpressionSyntax expression, Context context)
    {
        switch (context.Model.GetOperation(expression, context.CancellationToken))
        {
            case ISwitchExpressionOperation switchOp when context.Follows(switchOp.Value):
                return Facts.ThrowingCases(switchOp, context.Space, pattern => context.PatternOn(switchOp.Value, pattern));

            case IConditionalOperation { WhenFalse: { } whenFalse } conditional when Facts.IsThrow(conditional.WhenTrue) && !Facts.IsThrow(whenFalse):
                return context.Condition(conditional.Condition).AlwaysTrue;

            case IConditionalOperation { WhenFalse: { } whenFalse } conditional when Facts.IsThrow(whenFalse) && !Facts.IsThrow(conditional.WhenTrue):
                return context.Condition(conditional.Condition).AlwaysFalse;

            // `x ?? throw ...`
            case ICoalesceOperation coalesce when Facts.IsThrow(coalesce.WhenNull) && context.Value.IsReadBy(coalesce.Value):
                return CaseSet.Null;

            // `Guard.IsTrue(x is not Bird)`, with `[DoesNotReturnIf(false)] bool condition`: it doesn't return when the
            // condition is false. And `AssertSettled(x)`, a helper of one's own that throws for some cases.
            case IInvocationOperation { TargetMethod: var method } invocation when !Symbols.HasAttribute(method, "ConditionalAttribute", "System", "Diagnostics"):
                var throwing = Facts.ThrowsWhenCalled(invocation, context.Value, context.Space, context.Model.Compilation);
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Parameter?.GetAttributes().FirstOrDefault(a => Symbols.IsAttribute(a, "DoesNotReturnIfAttribute", "System", "Diagnostics", "CodeAnalysis")) is { ConstructorArguments.Length: 1 } doesNotReturnIf
                        && doesNotReturnIf.ConstructorArguments[0].Value is bool stopsWhen
                    )
                    {
                        var outcome = context.Condition(argument.Value);
                        throwing |= stopsWhen ? outcome.AlwaysTrue : outcome.AlwaysFalse;
                    }
                }

                return throwing;

            default:
                return CaseSet.Empty;
        }
    }

    /// <summary>
    /// The expressions a statement is sure to evaluate, outermost first:
    /// not ones in a branch of a conditional, the right of a short-circuiting operator, a lambda, a switch arm, or the arguments of a call that may be compiled out.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> UnconditionallyEvaluated(StatementSyntax statement, SemanticModel model, CancellationToken ct)
    {
        var roots = statement switch
        {
            ExpressionStatementSyntax expression => [expression.Expression],
            LocalDeclarationStatementSyntax declaration => declaration.Declaration.Variables.Select(v => v.Initializer?.Value).OfType<ExpressionSyntax>(),
            YieldStatementSyntax { Expression: { } expression } => [expression],
            _ => Enumerable.Empty<ExpressionSyntax>(),
        };

        var pending = new Stack<ExpressionSyntax>(roots.Reverse());
        while (pending.Count > 0)
        {
            var expression = pending.Pop();
            yield return expression;

            IEnumerable<ExpressionSyntax> children = expression switch
            {
                ParenthesizedExpressionSyntax parenthesized => [parenthesized.Expression],
                CastExpressionSyntax cast => [cast.Expression],
                AwaitExpressionSyntax await => [await.Expression],
                PostfixUnaryExpressionSyntax postfix => [postfix.Operand],
                PrefixUnaryExpressionSyntax prefix => [prefix.Operand],
                ConditionalExpressionSyntax conditional => [conditional.Condition],
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.CoalesceExpression) => [binary.Left],
                BinaryExpressionSyntax binary => [binary.Left, binary.Right],
                AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) => [assignment.Left],
                AssignmentExpressionSyntax assignment => [assignment.Left, assignment.Right],
                MemberAccessExpressionSyntax member => [member.Expression],
                ElementAccessExpressionSyntax element => element.ArgumentList.Arguments.Select(a => a.Expression).Prepend(element.Expression),
                InvocationExpressionSyntax invocation when model.GetOperation(invocation, ct) is IInvocationOperation { TargetMethod: var method }
                    && !Symbols.HasAttribute(method, "ConditionalAttribute", "System", "Diagnostics") =>
                    invocation.ArgumentList.Arguments.Select(a => a.Expression).Prepend(invocation.Expression),
                BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments.Select(a => a.Expression) ?? [],
                TupleExpressionSyntax tuple => tuple.Arguments.Select(a => a.Expression),
                SwitchExpressionSyntax switchExpression => [switchExpression.GoverningExpression],
                IsPatternExpressionSyntax isPattern => [isPattern.Expression],
                _ => [],
            };

            foreach (var child in children.Reverse())
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// The cases of <paramref name="value"/> that code before <paramref name="node"/> rules out, assuming <see cref="IsTrackable"/>.
    /// </summary>
    /// <remarks>
    /// Walks outward from <paramref name="node"/> one syntax level at a time, collecting what each level establishes on the way in (an earlier <c>if (x is T) &lt;exit&gt;</c> in the same statement list, for instance).
    /// It stops at a level whose part containing <paramref name="node"/> writes the value (anything established outside that part may no longer hold), at a label (a goto could land there without passing the earlier code), and at a function boundary (code outside the function doesn't run on the way in).
    /// </remarks>
    public static CaseSet Excluded(SyntaxNode node, TrackedValue value, CaseSpace space, SemanticModel model, CancellationToken ct)
    {
        var excluded = Excluded(node, value, space, model, ct, onEntry: false, copies: 0);
        return excluded | CaseSplits(node, value, space, model, ct, excluded);
    }

    /// <summary>
    /// The cases of the switch input <paramref name="input"/> that code before <paramref name="node"/> rules out, or none if the analysis can't follow the input.
    /// </summary>
    public static CaseSet ExcludedBefore(SyntaxNode node, IOperation input, CaseSpace space, SemanticModel model, Session session, CancellationToken ct) =>
        TrackedValue.From(input, session) is { } value && IsTrackable(value, node, model, ct)
            ? Excluded(node, value, space, model, ct)
            : CaseSet.Empty;

    /// <summary>
    /// Whether every use of the value is in plain sight, so that facts established about it hold until it's visibly written.
    /// It has to belong to the function that holds <paramref name="node"/> (not an enclosing function, and not a primary constructor parameter, which any member can write), unless nothing ever changes it (see <see cref="VariableUsage"/>).
    /// Nothing may capture it and write it, since a call could run the capturing code.
    /// And nothing may take a reference to it, since a write through that reference wouldn't look like a write to the value.
    /// </summary>
    /// <remarks>Readonly members can still change while the object is being constructed, and data flow analysis doesn't follow writes to them, so a chain of members is only trackable outside constructors and initializers.</remarks>
    public static bool IsTrackable(TrackedValue value, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (!value.Steps.IsEmpty && Functions.IsInConstruction(node) && !FollowableInConstructor(value, node, model, ct))
        {
            return false;
        }

        if (value.IndexVariables.Any(index => value.Session.UsageOf(index, node, model, ct) is null))
        {
            return false;
        }

        return value.Variable is null || value.Session.UsageOf(value.Variable, node, model, ct) is not null;
    }
}

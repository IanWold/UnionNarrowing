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
/// What a condition says about several values together, as boxes of combinations it's always true (or always false) for.
/// </summary>
file class Joint(IReadOnlyList<TrackedValue> values, IReadOnlyList<CaseSpace> spaces, SemanticModel model)
{
    private CaseSet[] Everything =>
        [.. spaces.Select(space => space.All)];

    private CaseSet[] At(int position, CaseSet cases)
    {
        var box = Everything;
        box[position] = cases;
        return box;
    }

    /// <summary>
    /// The boxes in both:
    /// each pair's overlap, if any.
    /// </summary>
    private static List<CaseSet[]> Cross(List<CaseSet[]> left, List<CaseSet[]> right) => [..
        left
        .SelectMany(l => right.Select(r => l.Zip(r, (a, b) => a & b).ToArray()))
        .Where(box => !IsEmpty(box))
        .Take(Limits.Boxes)
    ];

    private int IndexOf(IOperation element)
    {
        for (var k = 0; k < values.Count; k++)
        {
            if (values[k].IsReadBy(element))
            {
                return k;
            }
        }

        return -1;
    }

    private static bool IsEmpty(CaseSet[] box) =>
        box.Any(set => set.IsEmpty);

    /// <summary>
    /// A property pattern on what some of the values are members of (<c>b is { Left: Lit, Right: Lit }</c>, <c>e is Bin { Left: Lit, Right: Lit }</c>):
    /// true where every subpattern is, as long as the others match anything; false where any one subpattern is.
    /// Null if no subpattern is on one of the values.
    /// </summary>
    private (List<CaseSet[]> AlwaysTrue, List<CaseSet[]> AlwaysFalse)? OfMemberPatterns(IOperation input, IPatternOperation pattern)
    {
        if (pattern is not IRecursivePatternOperation { DeconstructionSubpatterns.IsEmpty: true } recursive || TrackedValue.From(input, values[0].Session) is not { } owner)
        {
            return null;
        }

        IReadOnlyList<Step> into = SymbolEqualityComparer.Default.Equals(recursive.MatchedType, recursive.InputType) ? [] : [new CaseStep(recursive.MatchedType)];
        var alwaysTrue = Everything;
        var sureToMatch = true;
        var alwaysFalse = new List<CaseSet[]>();

        foreach (var property in recursive.PropertySubpatterns)
        {
            IReadOnlyList<Step> path = [.. into, .. Facts.MemberChain(property.Member)];
            var k = values.ToList().FindIndex(value => owner.IsPrefixOf(value) && value.Steps.Skip(owner.Steps.Length).SequenceEqual(path));

            if (k < 0)
            {
                sureToMatch &= Facts.MatchesAnything(property.Pattern);
                continue;
            }

            var outcome = Facts.Of(property.Pattern, spaces[k], model.Compilation);
            
            alwaysTrue[k] &= outcome.AlwaysTrue;
            alwaysFalse.Add(At(k, outcome.AlwaysFalse));
        }

        return alwaysFalse.Count == 0 ? null : (sureToMatch && !IsEmpty(alwaysTrue) ? [alwaysTrue] : [], alwaysFalse);
    }

    /// <summary>
    /// A tuple pattern over the values (in the places <paramref name="positions"/> gives, or -1 for an element that isn't one):
    /// true where every element's subpattern is, false where any one's is.
    /// </summary>
    private (List<CaseSet[]> AlwaysTrue, List<CaseSet[]> AlwaysFalse) OfTuplePattern(IPatternOperation pattern, IReadOnlyList<int> positions)
    {
        switch (pattern)
        {
            case IDiscardPatternOperation or IDeclarationPatternOperation { MatchesNull: true }:
                return ([Everything], []);

            case IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true } recursive when recursive.DeconstructionSubpatterns.Length == positions.Count:
                var alwaysTrue = Everything;
                var sureToMatch = true;
                var alwaysFalse = new List<CaseSet[]>();

                for (var i = 0; i < positions.Count; i++)
                {
                    var subpattern = recursive.DeconstructionSubpatterns[i];
                    if (positions[i] < 0)
                    {
                        sureToMatch &= Facts.MatchesAnything(subpattern);
                        continue;
                    }

                    var outcome = Facts.Of(subpattern, spaces[positions[i]], model.Compilation);
                    
                    alwaysTrue[positions[i]] &= outcome.AlwaysTrue;
                    alwaysFalse.Add(At(positions[i], outcome.AlwaysFalse));
                }

                return (sureToMatch && !IsEmpty(alwaysTrue) ? [alwaysTrue] : [], alwaysFalse);

            case INegatedPatternOperation not:
                var inner = OfTuplePattern(not.Pattern, positions);
                return (inner.AlwaysFalse, inner.AlwaysTrue);

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and:
                var (andLeft, andRight) = (OfTuplePattern(and.LeftPattern, positions), OfTuplePattern(and.RightPattern, positions));
                return (Cross(andLeft.AlwaysTrue, andRight.AlwaysTrue), [.. andLeft.AlwaysFalse, .. andRight.AlwaysFalse]);

            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or:
                var (orLeft, orRight) = (OfTuplePattern(or.LeftPattern, positions), OfTuplePattern(or.RightPattern, positions));
                return ([.. orLeft.AlwaysTrue, .. orRight.AlwaysTrue], Cross(orLeft.AlwaysFalse, orRight.AlwaysFalse));

            default:
                return ([], []);
        }
    }

    public (List<CaseSet[]> AlwaysTrue, List<CaseSet[]> AlwaysFalse) Of(ExpressionSyntax condition, CancellationToken ct) =>
        model.GetOperation(WriteAnalysis.WithoutParentheses(condition), ct) is { } operation ? Of(operation) : ([], []);

    public (List<CaseSet[]> AlwaysTrue, List<CaseSet[]> AlwaysFalse) Of(IOperation condition)
    {
        switch (condition)
        {
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } not:
                var inner = Of(not.Operand);
                return (inner.AlwaysFalse, inner.AlwaysTrue);

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And, OperatorMethod: null } both when Facts.IsLogical(both):
                var (andLeft, andRight) = (Of(both.LeftOperand), Of(both.RightOperand));
                return (Cross(andLeft.AlwaysTrue, andRight.AlwaysTrue), [.. andLeft.AlwaysFalse, .. andRight.AlwaysFalse]);

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or, OperatorMethod: null } either when Facts.IsLogical(either):
                var (orLeft, orRight) = (Of(either.LeftOperand), Of(either.RightOperand));
                return ([.. orLeft.AlwaysTrue, .. orRight.AlwaysTrue], Cross(orLeft.AlwaysFalse, orRight.AlwaysFalse));

            case IIsPatternOperation { Value: ITupleOperation tuple } isPattern:
                return OfTuplePattern(isPattern.Pattern, [.. tuple.Elements.Select(IndexOf)]);

            case IIsPatternOperation isPattern when OfMemberPatterns(isPattern.Value, isPattern.Pattern) is { } onMembers:
                return onMembers;

            default:
                // A test of one value: a box with what it says at that value's place.
                var (alwaysTrue, alwaysFalse) = (new List<CaseSet[]>(), new List<CaseSet[]>());
                for (var k = 0; k < values.Count; k++)
                {
                    var outcome = Facts.Of(condition, values[k], spaces[k], model.Compilation);
                    if (!outcome.AlwaysTrue.IsEmpty)
                    {
                        alwaysTrue.Add(At(k, outcome.AlwaysTrue));
                    }

                    if (!outcome.AlwaysFalse.IsEmpty)
                    {
                        alwaysFalse.Add(At(k, outcome.AlwaysFalse));
                    }
                }

                return (alwaysTrue, alwaysFalse);
        }
    }

    /// <summary>
    /// A pattern on a tuple literal, some of whose elements are the values.
    /// </summary>
    public (List<CaseSet[]> AlwaysTrue, List<CaseSet[]> AlwaysFalse) OfTuple(IPatternOperation pattern, ITupleOperation tuple) =>
        OfTuplePattern(pattern, [.. tuple.Elements.Select(IndexOf)]);
}

internal static partial class Narrowing
{
    /// <summary>
    /// Cases ruled out piece by piece, over the values of bools that guards test along with the value:
    /// <c>if (pet is Bird &amp;&amp; flag) return 0; if (pet is Bird &amp;&amp; !flag) return 1;</c> rules out Bird, whatever flag is.
    /// Works out what the guards rule out for the value and the bools together, then keeps the cases that's true for with every combination of the bools.
    /// </summary>
    private static CaseSet CaseSplits(SyntaxNode node, TrackedValue value, CaseSpace space, SemanticModel model, CancellationToken ct, CaseSet already)
    {
        var flags = FlagsTestedBefore(node, model, ct);
        if (flags.Count == 0 || CaseSpace.OfElement(model.Compilation.GetSpecialType(SpecialType.System_Boolean), node.SpanStart, model, ct) is not { } boolSpace)
        {
            return CaseSet.Empty;
        }

        var values = new List<TrackedValue> { value };
        values.AddRange(flags.Select(flag => TrackedValue.OfVariable(flag, value.Session)));
        if (!values.Skip(1).All(v => IsTrackable(v, node, model, ct)))
        {
            return CaseSet.Empty;
        }

        var boxes = ExcludedTogether(node, values, [.. values.Select((_, i) => i == 0 ? space : boolSpace)], model, ct);
        if (boxes.Count == 0)
        {
            return CaseSet.Empty;
        }

        return CaseSet.Of(space.Cases.Where(candidate => !already.Contains(candidate) && Combinations.HoldWhateverTheFlags(boxes, candidate, flags.Count)));
    }

    /// <summary>
    /// The bool locals and parameters tested in the conditions of <c>if</c>s before the node, in its function (the first few).
    /// </summary>
    private static List<ISymbol> FlagsTestedBefore(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (Functions.EnclosingFunction(node) is not { } function)
        {
            return [];
        }

        var flags = new List<ISymbol>();
        foreach (var ifStatement in function.DescendantNodes(n => n == function || !Functions.IsFunction(n)).OfType<IfStatementSyntax>())
        {
            if (ifStatement.SpanStart >= node.SpanStart)
            {
                break;
            }

            foreach (var name in ifStatement.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(name, ct).Symbol is not { } flag
                    || flag is not (ILocalSymbol { Type.SpecialType: SpecialType.System_Boolean, IsRef: false } or IParameterSymbol { Type.SpecialType: SpecialType.System_Boolean, RefKind: RefKind.None })
                    || flags.Contains(flag, SymbolEqualityComparer.Default)
                )
                {
                    continue;
                }

                flags.Add(flag);
                if (flags.Count == Limits.Flags)
                {
                    return flags;
                }
            }
        }

        return flags;
    }

    /// <summary>
    /// The combinations of cases of several values a condition is always true for, as boxes (one set of cases per value).
    /// </summary>
    public static List<CaseSet[]> AlwaysTrueTogether(IOperation condition, IReadOnlyList<TrackedValue> values, IReadOnlyList<CaseSpace> spaces, SemanticModel model) =>
        new Joint(values, spaces, model).Of(condition).AlwaysTrue;

    /// <summary>
    /// For a switch over several values at once (a tuple), combinations of their cases that earlier code rules out together, beyond what it rules out for each one alone:
    /// <c>if (a is Bird &amp;&amp; b is Bird) return;</c> rules out (Bird, Bird).
    /// Each box is one set of cases per value.
    /// Comes from <c>if</c> guards and branches on the way out, stopping at anything that writes any of the values, at a label, and at the function's boundary.
    /// Assumes each value <see cref="IsTrackable"/>.
    /// </summary>
    public static IReadOnlyList<CaseSet[]> ExcludedTogether(SyntaxNode node, IReadOnlyList<TrackedValue> values, IReadOnlyList<CaseSpace> spaces, SemanticModel model, CancellationToken ct)
    {
        var boxes = new List<CaseSet[]>();
        var contexts = values.Select((value, i) => new Context(value, spaces[i], model, node, onEntry: false, usage: null, ct)).ToList();
        var joint = new Joint(values, spaces, model);
        var child = node;
        
        for (var parent = node.Parent; parent is not null && !Functions.IsFunction(parent); child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case BlockSyntax or GlobalStatementSyntax or SwitchSectionSyntax when child is StatementSyntax statement:
                    if (statement is LabeledStatementSyntax || contexts.Any(c => c.WritesBefore(statement)))
                    {
                        return boxes;
                    }

                    var statements = Statements.StatementsAround(parent)!;
                    for (var i = Statements.IndexOf(statements, statement) - 1; i >= 0; i--)
                    {
                        var previous = statements[i];
                        if (previous is LabeledStatementSyntax || contexts.Any(c => c.Writes(previous)))
                        {
                            return boxes;
                        }

                        // An if whose then (or else) exits: its end is only reached the other way.
                        if (previous is IfStatementSyntax guard)
                        {
                            if (contexts[0].Exits(guard.Statement) && (guard.Else is null || !contexts[0].Exits(guard.Else.Statement)))
                            {
                                boxes.AddRange(joint.Of(guard.Condition, ct).AlwaysTrue);
                            }
                            else if (guard.Else is { } otherwise && contexts[0].Exits(otherwise.Statement))
                            {
                                boxes.AddRange(joint.Of(guard.Condition, ct).AlwaysFalse);
                            }
                        }

                        // A switch statement over a tuple of the values that no section breaks out of: its end is only
                        // reached when no label matched.
                        if (previous is SwitchStatementSyntax earlierSwitch
                            && model.GetOperation(earlierSwitch, ct) is ISwitchOperation { Value: ITupleOperation earlierTuple } earlierOp
                            && !Statements.GotosIntoSections(earlierSwitch)
                            && !earlierSwitch.Sections.Any(section => Statements.BreaksOut(section, earlierSwitch))
                            && !Statements.HasDefault(earlierOp)
                        )
                        {
                            foreach (var clause in earlierOp.Cases.SelectMany(c => c.Clauses).OfType<IPatternCaseClauseOperation>().Where(c => c.Guard is null))
                            {
                                boxes.AddRange(joint.OfTuple(clause.Pattern, earlierTuple).AlwaysTrue);
                            }
                        }
                    }

                    // In a section of a switch statement over a tuple of the values: its one label matched, and the labels
                    // before it (with no `when` clause) didn't.
                    if (parent is SwitchSectionSyntax { Parent: SwitchStatementSyntax outerSwitch } section
                        && model.GetOperation(outerSwitch, ct) is ISwitchOperation { Value: ITupleOperation outerSwitchTuple } outerSwitchOp
                        && !Statements.GotosIntoSections(outerSwitch)
                        && !outerSwitch.Sections.SelectMany(s => s.Labels).OfType<CasePatternSwitchLabelSyntax>().Any(l => l.WhenClause is not null)
                    )
                    {
                        var sectionIndex = outerSwitch.Sections.IndexOf(section);
                        var clauses = outerSwitchOp.Cases[sectionIndex].Clauses;
                        if (clauses.Length == 1 && clauses[0] is IPatternCaseClauseOperation own)
                        {
                            boxes.AddRange(joint.OfTuple(own.Pattern, outerSwitchTuple).AlwaysFalse);
                            foreach (var earlier in outerSwitchOp.Cases.Take(sectionIndex).SelectMany(c => c.Clauses).OfType<IPatternCaseClauseOperation>())
                            {
                                boxes.AddRange(joint.OfTuple(earlier.Pattern, outerSwitchTuple).AlwaysTrue);
                            }
                        }
                    }

                    break;

                // `a is Bird && b is Bird ? 0 : (a, b) switch { ... }`, and the right of `&&` and `||`.
                case ConditionalExpressionSyntax conditional when child == conditional.WhenTrue || child == conditional.WhenFalse:
                    if (contexts.Any(c => c.WritesBefore(child) || c.Writes(conditional.Condition)))
                    {
                        return boxes;
                    }

                    var ofConditional = joint.Of(conditional.Condition, ct);
                    boxes.AddRange(child == conditional.WhenTrue ? ofConditional.AlwaysFalse : ofConditional.AlwaysTrue);
                    break;

                case BinaryExpressionSyntax binary when child == binary.Right && (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)):
                    if (contexts.Any(c => c.WritesBefore(child) || c.Writes(binary.Left)))
                    {
                        return boxes;
                    }

                    var ofLeft = joint.Of(binary.Left, ct);
                    boxes.AddRange(binary.IsKind(SyntaxKind.LogicalAndExpression) ? ofLeft.AlwaysFalse : ofLeft.AlwaysTrue);
                    break;

                case IfStatementSyntax ifStatement when child == ifStatement.Statement:
                    if (contexts.Any(c => c.WritesBefore(child) || c.Writes(ifStatement.Condition)))
                    {
                        return boxes;
                    }

                    boxes.AddRange(joint.Of(ifStatement.Condition, ct).AlwaysFalse);
                    break;

                case ElseClauseSyntax { Parent: IfStatementSyntax ifStatement }:
                    if (contexts.Any(c => c.WritesBefore(child) || c.Writes(ifStatement.Condition)))
                    {
                        return boxes;
                    }

                    boxes.AddRange(joint.Of(ifStatement.Condition, ct).AlwaysTrue);
                    break;

                // An arm of an outer switch over a tuple literal of the values: its pattern matched, and the earlier arms'
                // didn't. (Not past `when` clauses, which could write the values.)
                case SwitchExpressionArmSyntax arm when arm.Parent is SwitchExpressionSyntax outer
                    && model.GetOperation(outer, ct) is ISwitchExpressionOperation { Value: ITupleOperation outerTuple } outerOp:
                    var armIndex = outer.Arms.IndexOf(arm);
                    if (contexts.Any(c => c.WritesBefore(child)) || outer.Arms.Take(armIndex + 1).Any(a => a.WhenClause is not null))
                    {
                        return boxes;
                    }

                    boxes.AddRange(joint.OfTuple(outerOp.Arms[armIndex].Pattern, outerTuple).AlwaysFalse);
                    foreach (var earlier in outerOp.Arms.Take(armIndex))
                    {
                        boxes.AddRange(joint.OfTuple(earlier.Pattern, outerTuple).AlwaysTrue);
                    }

                    break;
            }

            if (boxes.Count > Limits.Boxes)
            {
                return [.. boxes.Take(Limits.Boxes)];
            }
        }

        return boxes;
    }
}

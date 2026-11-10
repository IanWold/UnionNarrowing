using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// How a local or parameter is used, as far as following its value goes.
/// </summary>
internal class VariableUsage
{
    public IReadOnlyList<SyntaxNode> Declarations { get; }

    public SyntaxNode DeclaringFunction { get; }

    /// <summary>
    /// Whether the variable keeps the value it starts with:
    /// nothing assigns it after its declaration, and the declaration can't run again for the same variable.
    /// Then what holds where a lambda is created holds whenever the lambda runs.
    /// </summary>
    public bool NeverChanges { get; }

    /// <summary>
    /// For a node in a lambda (or query) inside the function the variable belongs to, whether the variable keeps the value it has where the lambda is made:
    /// see <see cref="MadeAfterEveryWrite"/>.
    /// Then, as for one that <see cref="NeverChanges"/>, what holds there holds whenever the lambda runs.
    /// </summary>
    public bool SetForGoodWhereCaptured { get; }

    /// <summary>
    /// The uses of the variable that write it (assignments, <c>out</c> arguments, mutating calls).
    /// </summary>
    public IReadOnlyList<IdentifierNameSyntax> Writes { get; }

    private VariableUsage(SyntaxNode declaringFunction, bool neverChanges, IReadOnlyList<SyntaxNode> declarations, IReadOnlyList<IdentifierNameSyntax> writes, bool setForGoodWhereCaptured = false)
    {
        DeclaringFunction = declaringFunction;
        NeverChanges = neverChanges;
        SetForGoodWhereCaptured = setForGoodWhereCaptured;
        Declarations = declarations;
        Writes = writes;
    }

    /// <summary>
    /// Whether control can come back to the statement after it has run, within the same run of its statement list (so for the same variables):
    /// through a goto, located after it, to a label at or before it in that list, or for a switch section, through a <c>goto case</c> or <c>goto default</c> located after it.
    /// </summary>
    /// <remarks>Jumping to a label in an enclosing list leaves the list, and coming back into it starts a new run, with new variables.
    /// A goto before the statement has already run by the time the statement does (anything that sends control back before it is itself a goto after it).</remarks>
    private static bool CanRunAgain(StatementSyntax statement)
    {
        var (statements, scope) = statement.Parent switch
        {
            BlockSyntax block => ((IReadOnlyList<StatementSyntax>?)block.Statements, (SyntaxNode?)block),
            SwitchSectionSyntax { Parent: SwitchStatementSyntax switchStatement } section => (section.Statements, switchStatement),
            GlobalStatementSyntax { Parent: CompilationUnitSyntax unit } =>
                (unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement).ToList(), unit),
            _ => (null, null),
        };

        if (statements is null || scope is null)
        {
            return true;
        }

        var labels = statements.Take(Statements.IndexOf(statements, statement) + 1)
            .OfType<LabeledStatementSyntax>()
            .Select(label => label.Identifier.ValueText)
            .ToList();

        return scope.DescendantNodes(n => n == scope || !Functions.IsFunction(n))
            .OfType<GotoStatementSyntax>()
            .Where(jump => jump.SpanStart > statement.SpanStart)
            .Any(jump => jump.IsKind(SyntaxKind.GotoStatement)
                ? jump.Expression is IdentifierNameSyntax target && labels.Contains(target.Identifier.ValueText)
                : statement.Parent is SwitchSectionSyntax
            );
    }

    /// <summary>
    /// Whether the declaration gives each variable its value just once.
    /// A parameter's does;
    /// so does a foreach or using variable's (and they can't be assigned).
    /// A local's might run again for the same variable if a goto can jump back over it within its scope.
    /// </summary>
    private static bool DeclarationRunsOnce(ISymbol variable, IReadOnlyList<SyntaxNode> declarations) =>
        variable switch
        {
            IParameterSymbol => true,
            ILocalSymbol { IsForEach: true } or ILocalSymbol { IsUsing: true } => true,
            ILocalSymbol => declarations.All(declaration =>
                declaration.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax { Parent: BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax })
                    is StatementSyntax statement
                && !CanRunAgain(statement)
            ),
            _ => false,
        };

    /// <summary>
    /// Whether every write to the variable is done by the time the lambda (or query) <paramref name="function"/> is made, for good:
    /// each one comes before it, no loop around it holds one (and could run it again), the function the variable belongs to has no goto, and every function from this one out to that one is a lambda or query (a local function could be called before the writes).
    /// </summary>
    private static bool MadeAfterEveryWrite(SyntaxNode? function, SyntaxNode declaringFunction, IReadOnlyList<IdentifierNameSyntax> writes)
    {
        if (function is null || declaringFunction.DescendantNodes(n => n == declaringFunction || !Functions.IsFunction(n)).Any(n => n is GotoStatementSyntax))
        {
            return false;
        }

        for (var outer = function; outer != declaringFunction; outer = Functions.EnclosingFunction(outer))
        {
            if (outer is not (AnonymousFunctionExpressionSyntax or QueryExpressionSyntax))
            {
                return false;
            }

            if (Functions.EnclosingFunction(outer) == declaringFunction)
            {
                var loops = outer.Ancestors().TakeWhile(a => a != declaringFunction).Where(a => a is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax);
                return writes.All(write => StoredBy(write).Span.End <= outer.SpanStart) && !loops.Any(loop => writes.Any(write => loop.Span.Contains(write.Span)));
            }
        }

        return false;
    }

    /// <summary>
    /// What a write has done by the time it ends: the statement it's in (an assignment stores only once its right side, which a lambda can be in, is worked out).
    /// </summary>
    private static SyntaxNode StoredBy(IdentifierNameSyntax write) =>
        (SyntaxNode?)write.FirstAncestorOrSelf<StatementSyntax>() ?? (SyntaxNode?)write.FirstAncestorOrSelf<ArrowExpressionClauseSyntax>() ?? write;

    private static List<SyntaxNode> DeclarationsIn(SyntaxTree tree, ISymbol variable, CancellationToken ct) =>
        [.. variable.DeclaringSyntaxReferences.Where(r => r.SyntaxTree == tree).Select(r => r.GetSyntax(ct))];

    private static SyntaxNode? DeclaringFunctionAround(SyntaxNode node, IEnumerable<SyntaxNode> declarations) =>
        declarations.Select(Functions.EnclosingFunction).FirstOrDefault(f => f is not null && f.Span.Contains(node.Span));

    /// <summary>
    /// How <paramref name="variable"/> is used, or null if the analysis can't follow it to <paramref name="node"/>.
    /// That depends only on where <paramref name="node"/> is, as <see cref="Locate"/> finds, which is how <see cref="Session.UsageOf"/> can work it out once for many nodes.
    /// </summary>
    public static VariableUsage? Analyze(ISymbol variable, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        var declarations = DeclarationsIn(node.SyntaxTree, variable, ct);

        // A query's range variable, which is a parameter of a lambda the query turns into: it can't be assigned, or
        // passed by reference.
        if (declarations.Count == 0 && variable is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } })
        {
            return Functions.EnclosingFunction(node) is QueryExpressionSyntax query ? new VariableUsage(query, neverChanges: true, declarations, []) : null;
        }

        if (DeclaringFunctionAround(node, declarations) is not { } declaringFunction)
        {
            return null;
        }

        IEnumerable<SyntaxNode> body = declaringFunction is CompilationUnitSyntax unit ? unit.Members.OfType<GlobalStatementSyntax>() : [declaringFunction];
        var uses = body.SelectMany(part => Symbols.References(variable, part, model, ct)).ToList();

        if (uses.Any(use => WriteAnalysis.TakesReference(use, model, ct)))
        {
            return null;
        }

        var writes = uses.Where(use => WriteAnalysis.IsWrite(use, model, ct)).ToList();
        var neverChanges = writes.Count == 0 && DeclarationRunsOnce(variable, declarations);

        var function = Functions.EnclosingFunction(node);
        if (function != declaringFunction)
        {
            // The switch is in a function inside the one the variable belongs to: a lambda, a local function, or for
            // a primary constructor parameter, any member. Facts from out there only reach in across lambdas and
            // queries, which run after they're created (see CanWalkOutOf); either way, nothing can have changed the
            // value in between, as long as nothing ever does. Or, for a local function that's only ever called
            // directly (and runs right then), from each of its calls.
            if (!neverChanges)
            {
                if (writes.Any(write => Functions.EnclosingFunction(write) != declaringFunction))
                {
                    return null;
                }

                if (MadeAfterEveryWrite(function, declaringFunction, writes))
                {
                    return new VariableUsage(declaringFunction, neverChanges, declarations, writes, setForGoodWhereCaptured: true);
                }

                if (!Functions.RunsWhenCalled(function, declaringFunction, model, ct))
                {
                    return null;
                }
            }
            else if (declaringFunction is TypeDeclarationSyntax && variable.ContainingType?.IsValueType != false)
            {
                // A struct's primary constructor parameters live in `this`, which `this = ...` can replace.
                return null;
            }
        }
        else if (writes.Any(write => Functions.EnclosingFunction(write) != declaringFunction))
        {
            // A lambda or local function that writes it could run between the facts and the switch.
            return null;
        }

        return new VariableUsage(declaringFunction, neverChanges, declarations, writes);
    }

    /// <summary>
    /// Where <paramref name="node"/> is, as far as how <paramref name="variable"/> is used there goes:
    /// the function that declares the variable, if that's around the node, and the function the node is in.
    /// </summary>
    public static (SyntaxNode? DeclaringFunction, SyntaxNode? Function) Locate(ISymbol variable, SyntaxNode node, CancellationToken ct) =>
        (DeclaringFunctionAround(node, DeclarationsIn(node.SyntaxTree, variable, ct)), Functions.EnclosingFunction(node));
}

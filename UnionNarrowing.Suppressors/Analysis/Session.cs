using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// One run of the analysis, from one entry point (the suppressor, an analyzer for a compilation, a code fix):
/// the options from .editorconfig it runs with, and what it has worked out that can't change while it runs.
/// Every <see cref="TrackedValue"/> belongs to one.
/// </summary>
/// <param name="followsCallers">Whether what a method's or constructor's callers pass it counts as known about its parameters (see Narrowing).
/// Local functions and lambdas get what their callers pass either way: those are in plain sight, in the function they're written in.</param>
internal class Session(AnalyzerConfigOptionsProvider options, bool followsCallers = true)
{
    /// <summary>
    /// What <see cref="VariableUsage.Analyze"/> depends on:
    /// the variable, and where the node is.
    /// </summary>
    private readonly struct UsageKey(ISymbol variable, SyntaxTree tree, SyntaxNode? declaringFunction, SyntaxNode? function) : IEquatable<UsageKey>
    {
        private readonly SyntaxNode? _declaringFunction = declaringFunction;
        private readonly SyntaxNode? _function = function;
        private readonly SyntaxTree _tree = tree;
        private readonly ISymbol _variable = variable;

        public bool Equals(UsageKey other) =>
            SymbolEqualityComparer.Default.Equals(_variable, other._variable) && _tree == other._tree
            && _declaringFunction == other._declaringFunction && _function == other._function;

        public override bool Equals(object? obj) =>
            obj is UsageKey other && Equals(other);

        public override int GetHashCode() =>
            (((SymbolEqualityComparer.Default.GetHashCode(_variable) * 31) + _tree.GetHashCode()) * 31 + (_declaringFunction?.GetHashCode() ?? 0)) * 31 + (_function?.GetHashCode() ?? 0);
    }

    /// <summary>
    /// <c>unionnarrowing.follow_mutable_state = true</c> makes the analysis follow values that only another thread (or code the analysis can't see run) could change between a test and a switch:
    /// fields that aren't readonly, settable auto-properties, <c>this</c> in a struct (a union's own members), and array elements.
    /// Nothing that could run other code may come between the test and the switch.
    /// </summary>
    public const string FollowMutableStateKey = "unionnarrowing.follow_mutable_state";

    private readonly ConcurrentDictionary<SyntaxTree, bool> _followsMutableState = new();

    private ImmutableHashSet<string>? _unsafeAccessorTargets;

    /// <summary>
    /// Whether what a method's or constructor's callers pass it counts as known about its parameters.
    /// </summary>
    public bool FollowsCallers =>
        followsCallers;

    private readonly ConcurrentDictionary<UsageKey, VariableUsage?> _usages = new();

    private bool ReadFollowsMutableState(SyntaxTree tree) =>
        (options.GetOptions(tree).TryGetValue(FollowMutableStateKey, out var value) || options.GlobalOptions.TryGetValue(FollowMutableStateKey, out value))
        && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether, in <paramref name="tree"/>, the analysis follows values that only something else could change (see <see cref="FollowMutableStateKey"/>).
    /// </summary>
    public bool FollowsMutableState(SyntaxTree tree) =>
        _followsMutableState.GetOrAdd(tree, ReadFollowsMutableState);

    /// <summary>
    /// The names of the methods <c>[UnsafeAccessor]</c> declarations in the compilation call, whatever their types (an empty name for one that can't be told).
    /// </summary>
    private static ImmutableHashSet<string> UnsafeAccessorTargets(Compilation compilation, CancellationToken ct) => [..
        compilation.SyntaxTrees
        .SelectMany(tree => tree.GetRoot(ct).DescendantNodes(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax)).OfType<AttributeSyntax>())
        .Where(attribute => attribute.Name.ToString() is "UnsafeAccessor" or "UnsafeAccessorAttribute" || attribute.Name.ToString().EndsWith(".UnsafeAccessor", StringComparison.Ordinal))
        .Select(attribute => attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name") is { } named
            ? (named.Expression as LiteralExpressionSyntax)?.Token.ValueText ?? ""
            : (attribute.Parent?.Parent as MethodDeclarationSyntax)?.Identifier.ValueText ?? ""
        )
    ];

    /// <summary>
    /// Whether an <c>[UnsafeAccessor]</c> in the compilation could call a method named <paramref name="name"/>, in whatever type, worked out once.
    /// </summary>
    public bool IsUnsafeAccessorTarget(string name, Compilation compilation, CancellationToken ct)
    {
        var targets = _unsafeAccessorTargets ??= UnsafeAccessorTargets(compilation, ct);
        return targets.Contains(name) || targets.Contains("");
    }

    /// <summary>
    /// How <paramref name="variable"/> is used, as far as following it to <paramref name="node"/> goes (see <see cref="VariableUsage.Analyze"/>), worked out once for every node in the same place.
    /// </summary>
    public VariableUsage? UsageOf(ISymbol variable, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        var (declaringFunction, function) = VariableUsage.Locate(variable, node, ct);
        return _usages.GetOrAdd(new UsageKey(variable, node.SyntaxTree, declaringFunction, function), _ => VariableUsage.Analyze(variable, node, model, ct));
    }
}

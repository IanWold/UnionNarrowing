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
    /// For a parameter of a local function or private method that's only ever called directly (never turned into a delegate, and not from inside itself), what holds for the argument at every call.
    /// A private method's calls have to be in one file.
    /// </summary>
    private static CaseSet FactsFromCallers(IMethodSymbol function, IParameterSymbol parameter, Context context)
    {
        var model = context.Model;
        var ct = context.CancellationToken;

        function = function.OriginalDefinition;

        var body = function.DeclaringSyntaxReferences.Length == 1 ? function.DeclaringSyntaxReferences[0].GetSyntax(ct) : null;
        if (body is null || Functions.CallerScopes(function, body, model, ct) is not { } scopes)
        {
            return CaseSet.Empty;
        }

        var calls = new List<(SyntaxNode Call, SemanticModel Model)>();

        foreach (var scope in scopes)
        {
            var scopeModel = Functions.SemanticModelFor(scope, model);
            var found = function.MethodKind == MethodKind.Constructor
                ? Functions.ConstructorCalls(function, scope, body, scopeModel, ct)
                : Functions.DirectCalls(function, scope, body, scopeModel, ct)?.Cast<SyntaxNode>().ToList();

            if (found is null)
            {
                return CaseSet.Empty;
            }

            calls.AddRange(found.Select(call => (call, scopeModel)));
        }

        CaseSet? atEntry = null;

        foreach (var (call, callModel) in calls)
        {
            var arguments = callModel.GetOperation(call, ct) switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                _ => default,
            };

            if (arguments.IsDefault || arguments.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.OriginalDefinition, parameter.OriginalDefinition)) is not { ArgumentKind: ArgumentKind.Explicit } argument)
            {
                return CaseSet.Empty;
            }

            // What's ruled out for a followed argument there (with the whole path the value is reached by, steps into a
            // union's case included), or what anything else passed is known not to be: a new case, a call to a method of
            // one's own, ...
            var atCall = TrackedValue.From(argument.Value, context.Value.Session) is { } passed
                ? IsTrackable(passed, argument.Value.Syntax, callModel, ct)
                    ? Excluded(argument.Value.Syntax, context.Value.WithRoot(passed), context.Space, callModel, ct, onEntry: false, context.Copies + 1)
                    : CaseSet.Empty
                : KnownExcluded(argument.Value, context.Value.Steps, context.Space, callModel, context.Value.Session, ct, context.Copies + 1);

            if (NotNullThere(argument.Value, context.Space, callModel, ct))
            {
                atCall |= CaseSet.Null;
            }

            atEntry = Meet(atEntry, atCall);
        }

        return atEntry ?? CaseSet.Empty;
    }

    /// <summary>
    /// For a variable a local function captures, what holds for it at every call to the function (which <see cref="Functions.RunsWhenCalled"/>).
    /// </summary>
    private static CaseSet FactsFromCallsOf(LocalFunctionStatementSyntax local, Context context)
    {
        var model = context.Model;
        var ct = context.CancellationToken;

        if (context.Value.IsExternallyMutable
            || model.GetDeclaredSymbol(local, ct) is not IMethodSymbol symbol
            || Functions.EnclosingFunction(local) is not { } scope
            || Functions.DirectCalls(symbol, scope, local, model, ct) is not { Count: > 0 } calls
        )
        {
            return CaseSet.Empty;
        }

        CaseSet? atEntry = null;
        foreach (var call in calls)
        {
            var atCall = IsTrackable(context.Value, call, model, ct)
                ? Excluded(call, context.Value, context.Space, model, ct, onEntry: false, context.Copies + 1)
                : CaseSet.Empty;

            atEntry = Meet(atEntry, atCall);
        }

        return atEntry ?? CaseSet.Empty;
    }

    /// <summary>
    /// For a lambda's parameter, where the lambda is only ever called with arguments that can be followed, what holds for them at every call:
    /// a lambda in a local that never changes and is only ever invoked (<c>Func&lt;Pet, int&gt; f = p =&gt; ...; f(pet)</c>), or a lambda passed to a method of one's own that only invokes it with one of its own parameters (<c>Run(pet, p =&gt; ...)</c>, with <c>R Run&lt;T, R&gt;(T value, Func&lt;T, R&gt; f) =&gt; f(value)</c>).
    /// </summary>
    private static CaseSet FactsFromDelegateCalls(IParameterSymbol parameter, Context context)
    {
        var model = context.Model;
        var ct = context.CancellationToken;
        var operation = Functions.PassedLambda(parameter, context.Start, model, ct);

        // What's passed for the parameter, at each call: the expressions, or null if some call can't be followed.
        var arguments = operation?.Parent switch
        {
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } } => LocalDelegateArguments(local),
            IArgumentOperation { Parameter: { } delegateParameter, Parent: IInvocationOperation call } => HelperArguments(call, delegateParameter),
            _ => null,
        };

        if (arguments is not { Count: > 0 })
        {
            return CaseSet.Empty;
        }

        CaseSet? atEntry = null;

        foreach (var (argument, site) in arguments)
        {
            atEntry = Meet(
                atEntry,
                TrackedValue.From(argument, context.Value.Session) is { } passed && context.Value.Rebased(TrackedValue.OfVariable(parameter, context.Value.Session), passed) is var path
                    && IsTrackable(path, site.Syntax, model, ct)
                        ? Excluded(site.Syntax, path, context.Space, model, ct, onEntry: false, context.Copies + 1)
                        : CaseSet.Empty
            );
        }

        return atEntry ?? CaseSet.Empty;

        // Every use of the local is a call (`f(pet)`, `f.Invoke(pet)`), and it's never assigned again.
        List<(IOperation, IOperation)>? LocalDelegateArguments(ILocalSymbol local)
        {
            if (local.DeclaringSyntaxReferences is not { Length: 1 } references
                || references[0].GetSyntax(ct) is not { } declarator
                || context.Value.Session.UsageOf(local, declarator, model, ct) is not { NeverChanges: true }
                || Functions.EnclosingFunction(declarator) is not { } scope
            )
            {
                return null;
            }

            var found = new List<(IOperation, IOperation)>();
            foreach (var name in Symbols.References(local, scope, model, ct))
            {
                if (model.GetOperation(name, ct) is not { Parent: IInvocationOperation { TargetMethod.Name: "Invoke" } invoke } reference
                    || invoke.Instance != reference
                    || invoke.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == parameter.Ordinal) is not { } argument
                )
                {
                    return null;
                }

                found.Add((argument.Value, invoke));
            }

            return found;
        }

        // A method declared here, whose every use of the delegate parameter is to invoke it with one of the method's own
        // by-value parameters that it never assigns: what this call passes for those.
        List<(IOperation, IOperation)>? HelperArguments(IInvocationOperation call, IParameterSymbol delegateParameter)
        {
            var method = call.TargetMethod.OriginalDefinition;
            if (Functions.OwnDeclaration(method, model.Compilation, ct) is not { } declaration)
            {
                return null;
            }

            var helperModel = Functions.SemanticModelFor(declaration, model);
            var delegateDefinition = method.Parameters[delegateParameter.Ordinal];

            if (context.Value.Session.UsageOf(delegateDefinition, declaration, helperModel, ct) is not { NeverChanges: true })
            {
                return null;
            }

            var passedOn = new List<IParameterSymbol>();
            foreach (var name in Symbols.References(delegateDefinition, declaration, helperModel, ct))
            {
                if (helperModel.GetOperation(name, ct) is not { Parent: IInvocationOperation { TargetMethod.Name: "Invoke" } invoke } reference
                    || invoke.Instance != reference
                    || invoke.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == parameter.Ordinal)?.Value is not IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } own }
                    || !SymbolEqualityComparer.Default.Equals(own.ContainingSymbol, method)
                    || context.Value.Session.UsageOf(own, declaration, helperModel, ct) is not { NeverChanges: true }
                )
                {
                    return null;
                }

                passedOn.Add(own);
            }

            var found = new List<(IOperation, IOperation)>();
            foreach (var own in passedOn.Distinct<IParameterSymbol>(SymbolEqualityComparer.Default))
            {
                if (call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == own.Ordinal) is not { ArgumentKind: ArgumentKind.Explicit } argument)
                {
                    return null;
                }

                found.Add((argument.Value, argument.Value));
            }

            return found;
        }
    }

    /// <summary>
    /// What's known about a parameter on entry to its function:
    /// for a lambda passed to a LINQ method, the Where filters its elements passed;
    /// for a local function or private method, what holds for the argument at every call.
    /// Nothing, for a value only something else could change (see <see cref="TrackedValue.IsExternallyMutable"/>):
    /// other code can run between the call and the read.
    /// </summary>
    private static CaseSet FactsOnEntry(Context context) =>
        context.Value.IsExternallyMutable || context.Value.Variable is not IParameterSymbol { ContainingSymbol: IMethodSymbol function } parameter
        ? CaseSet.Empty
        : function.MethodKind switch
        {
            MethodKind.AnonymousFunction => FactsFromLinqSource(parameter, context) | FactsFromDelegateCalls(parameter, context),
            MethodKind.LocalFunction or MethodKind.Ordinary or MethodKind.Constructor => FactsFromCallers(function, parameter, context),
            _ => CaseSet.Empty,
        };

    /// <summary>
    /// Whether the compiler's flow state says the value can't be null where it's evaluated, for a value whose null can only mean "nothing there" (see <see cref="CaseSpace.NullOnlyAsNoValue"/>).
    /// </summary>
    private static bool NotNullThere(IOperation value, CaseSpace space, SemanticModel model, CancellationToken ct) =>
        space.NullOnlyAsNoValue && model.GetTypeInfo(value.Syntax, ct).Nullability.FlowState == NullableFlowState.NotNull;
}

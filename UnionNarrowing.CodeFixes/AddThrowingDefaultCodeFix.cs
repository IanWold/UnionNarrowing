using System.Collections.Immutable;

namespace UnionNarrowing.CodeFixes;

/// <summary>
/// A switch statement over a union or closed hierarchy that handles every case (or every case earlier code leaves) still lets control fall out of its end, as far as definite assignment goes:
/// "not all code paths return a value" (CS0161, or CS1643 in a lambda) after one at the end of a method, and "use of unassigned local variable" (CS0165) after one that assigns in every section.
/// These are errors, so nothing can suppress them;
/// this adds the throwing <c>default:</c> section they need.
/// (UN0002 then reports any case added later that ends up there.)
/// </summary>
/// <remarks>
/// Fix All adds every throw in one pass, and a using for the exception, where the file needs one, once:
/// merged, what each fix does on its own would add the using once per fix, and the copies collide.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddThrowingDefaultCodeFix)), Shared]
public sealed class AddThrowingDefaultCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["CS0161", "CS1643", "CS0165"];

    /// <summary>
    /// For CS0161 on a method (or local function, or accessor):
    /// its body.
    /// </summary>
    private static BlockSyntax? BodyOf(SyntaxNode node) =>
        node.AncestorsAndSelf().Select(n => n switch
        {
            BaseMethodDeclarationSyntax method => method.Body,
            LocalFunctionStatementSyntax local => local.Body,
            AccessorDeclarationSyntax accessor => accessor.Body,
            AnonymousFunctionExpressionSyntax lambda => lambda.Block,
            _ => null,
        }).FirstOrDefault(b => b is not null);

    /// <summary>
    /// The statement control falls out of the end of <paramref name="statement"/> from:
    /// the statement itself, or for a block, a using, lock, fixed, checked, unsafe or labeled statement, or a try with no catch, the statement its own body ends with.
    /// </summary>
    private static StatementSyntax? EndOf(StatementSyntax? statement) => statement switch
    {
        BlockSyntax block => EndOf(block.Statements.LastOrDefault()),
        UsingStatementSyntax @using => EndOf(@using.Statement),
        LockStatementSyntax @lock => EndOf(@lock.Statement),
        FixedStatementSyntax @fixed => EndOf(@fixed.Statement),
        CheckedStatementSyntax @checked => EndOf(@checked.Block),
        UnsafeStatementSyntax @unsafe => EndOf(@unsafe.Block),
        LabeledStatementSyntax labeled => EndOf(labeled.Statement),
        TryStatementSyntax { Catches.Count: 0 } @try => EndOf(@try.Block),
        _ => statement,
    };

    /// <summary>
    /// For CS0165 on a read of a local:
    /// where control falls out of the statement just before the statement the read is in.
    /// </summary>
    private static StatementSyntax? EndOfStatementBefore(SyntaxNode node)
    {
        foreach (var statement in node.AncestorsAndSelf().OfType<StatementSyntax>())
        {
            if (statement.Parent is BlockSyntax block)
            {
                var index = block.Statements.IndexOf(statement);
                return index > 0 ? EndOf(block.Statements[index - 1]) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// What the fix for each diagnostic adds a throw to, where it's offered:
    /// a switch statement that handles every case, which gets a throwing default section, or a body that ends in ifs that test every case and return (<c>if (pet is Cat) return 1; ... if (pet is Bird) return 3;</c>), which gets a throw after them.
    /// Either can be inside the statement the body ends with (see <see cref="EndOf"/>): <c>using (...) { switch ... }</c>.
    /// </summary>
    private static async Task<HashSet<SyntaxNode>> TargetsAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken ct)
    {
        var targets = new HashSet<SyntaxNode>();
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return targets;
        }

        var session = new Session(document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        foreach (var diagnostic in diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var atEndOfBody = diagnostic.Id is "CS0161" or "CS1643";
            var end = atEndOfBody ? EndOf(BodyOf(node)) : EndOfStatementBefore(node);

            if (atEndOfBody && end?.Parent is BlockSyntax block
                && Handling.IfRunBefore(block.Statements, block.Statements.Count, model, session, ct) is { } handling
                && handling.Caught(handling.Excluded(model, ct)).Count == 0
            )
            {
                targets.Add(block);
            }
            else if (end is SwitchStatementSyntax candidate
                && !candidate.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax))
                && SwitchCoverage.Of(candidate, model, session, ct, countGuarded: false) is { OverCases: true, Complete: true }
            )
            {
                targets.Add(candidate);
            }
        }

        return targets;
    }

    /// <summary>
    /// Adds a throwing default section to each switch statement, and a throw to the end of each body.
    /// </summary>
    private static async Task<Document> ThrowAsync(Document document, IReadOnlyCollection<SyntaxNode> targets, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        var exception = compilation?.GetTypeByMetadataName("System.Diagnostics.UnreachableException") is not null
            ? "System.Diagnostics.UnreachableException"
            : "System.InvalidOperationException";
        var thrown = ThrowStatement(CodeFixSyntax.NewException(exception));
        var newRoot = root!.ReplaceNodes(targets, (_, current) => current switch
        {
            SwitchStatementSyntax switchStatement => switchStatement.AddSections(SwitchSection(SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()), SingletonList<StatementSyntax>(thrown)).WithAdditionalAnnotations(Formatter.Annotation)),
            BlockSyntax body => body.AddStatements(thrown.WithAdditionalAnnotations(Formatter.Annotation)),
            _ => current,
        });
        var newDocument = await CodeFixSyntax.ShortenNamesAsync(document.WithSyntaxRoot(newRoot), ct).ConfigureAwait(false);
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: ct).ConfigureAwait(false);
    }

    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) =>
            await ThrowAsync(document, await TargetsAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false)
        );

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var targets = await TargetsAsync(context.Document, context.Diagnostics, context.CancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                targets.First() is BlockSyntax ? "Throw after the ifs" : "Add a throwing default section",
                ct => ThrowAsync(context.Document, targets, ct),
                equivalenceKey: nameof(AddThrowingDefaultCodeFix)
            ),
            context.Diagnostics
        );
    }
}

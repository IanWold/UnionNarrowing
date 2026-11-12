using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using UnionNarrowing.Suppressors;
using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// .editorconfig settings, the same for every file.
/// </summary>
file class EditorConfig(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
{
    private readonly Options _options = new(values);

    public override AnalyzerConfigOptions GlobalOptions =>
        _options;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        _options;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        _options;
}

/// <summary>
/// The severities that .editorconfig settings like <c>dotnet_diagnostic.UN0020.severity = none</c> give, the same for every file, which the compiler applies itself rather than handing them to analyzers.
/// Only <c>none</c> is read.
/// </summary>
file class DiagnosticSeverities(IReadOnlyDictionary<string, string> values) : SyntaxTreeOptionsProvider
{
    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
        GeneratedKind.Unknown;

    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
    {
        severity = ReportDiagnostic.Suppress;
        return values.TryGetValue($"dotnet_diagnostic.{diagnosticId}.severity", out var value) && value == "none";
    }

    public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
    {
        severity = ReportDiagnostic.Default;
        return false;
    }
}

/// <summary>
/// Hands Fix All the diagnostics already found.
/// </summary>
file class GivenDiagnostics(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
{
    public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

    public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

    public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>([]);
}

file class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
        values.TryGetValue(key, out value);
}

/// <summary>
/// Compiles test snippets and checks what the suppressor, the analyzers and the code fixes make of them.
/// Every snippet gets <see cref="Usings"/> before it and the shared <see cref="Types"/> (the <c>Pet</c> union) after it.
/// </summary>
internal static partial class Verify
{
    private const string BecauseMarker = "/*because*/";

    /// <summary>
    /// UN0020, which reports what a switch misses instead of the compiler, where earlier code rules some of it out.
    /// </summary>
    private const string ReportedInstead = "UN0020";

    /// <summary>
    /// The suppressions of CS8509 and CS8846 on a switch that UN0020 is reported on instead.
    /// </summary>
    private static readonly string[] SuppressedForReportedInstead = ["UN0021", "UN0022"];

    private const string SuppressedMarker = "/*suppressed*/";

    /// <summary>
    /// Declarations every snippet can use.
    /// They go after the snippet, because top-level statements have to come before type declarations.
    /// </summary>
    private const string Types = """

        public interface IFlier { }
        public record Cat(string Name);
        public record Dog(string Name);
        public record Bird(string Name) : IFlier;
        public union Pet(Cat, Dog, Bird);
        """;

    private const string Usings = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Linq;

        """;

    private const string WarnsMarker = "/*warns*/";

    private static readonly ImmutableArray<MetadataReference> FrameworkReferences = LoadFrameworkReferences();

    /// <summary>
    /// The IDE's "populate switch" analyzers (IDE0072 for switch expressions, IDE0010 for statements).
    /// They're internal to Roslyn's Features layer, so they're created by name.
    /// </summary>
    private static readonly ImmutableArray<DiagnosticAnalyzer> IdePopulateSwitchAnalyzers =
    [
        .. new[] { "CSharpPopulateSwitchExpressionDiagnosticAnalyzer", "CSharpPopulateSwitchStatementDiagnosticAnalyzer" }.Select(name =>
            (DiagnosticAnalyzer)Activator.CreateInstance(
                Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features").GetTypes().Single(t => t.Name == name),
                nonPublic: true
            )!
        ),
    ];

    /// <summary>
    /// The IDE's "populate switch" analyzer for switch statements (IDE0010).
    /// </summary>
    public static DiagnosticAnalyzer IdePopulateSwitchStatement =>
        IdePopulateSwitchAnalyzers[1];

    /// <summary>
    /// Every diagnostic, from the compiler and <paramref name="analyzer"/>, with the suppressor's suppressions, and UN0020 where it's reported instead of a suppressed CS8509.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> AfterTheSuppressor(Document document, DiagnosticAnalyzer? analyzer, CancellationToken ct)
    {
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new UnionNarrowingSuppressor(), analyzer as MissingCasesAnalyzer ?? new MissingCasesAnalyzer());
        if (analyzer is not null and not MissingCasesAnalyzer)
        {
            analyzers = analyzers.Add(analyzer);
        }

        var options = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            onAnalyzerException: null,
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: true
        );
        return await (await document.Project.GetCompilationAsync(ct))!.WithAnalyzers(analyzers, options).GetAllDiagnosticsAsync(ct);
    }

    private static async Task<Document> Apply(CodeAction action, Document document, CancellationToken ct)
    {
        var operation = Assert.Single((await action.GetOperationsAsync(ct)).OfType<ApplyChangesOperation>());
        return operation.ChangedSolution.GetDocument(document.Id)!;
    }

    /// <summary>
    /// The fixed code has to be <paramref name="expected"/>, compile, and no longer have anything the fix is for, nor any warning <paramref name="document"/> didn't have, once the suppressor has had its say:
    /// an arm Add missing arms leaves out because earlier code ruled its case out still gets CS8509 from the compiler, which the suppressor has to agree with and silence.
    /// What's compiled is the fixed text parsed again, not the fix's syntax tree, which can hold nodes that the text they print as doesn't parse back to.
    /// </summary>
    private static async Task CheckFixed(Document document, Document fixedDocument, string expected, CodeFixProvider fix, DiagnosticAnalyzer? analyzer, CancellationToken ct, bool someLeftAlone = false, bool usings = true)
    {
        var fixedText = (await fixedDocument.GetTextAsync(ct)).ToString();
        Assert.Equal((usings ? Usings : "") + expected + Types, fixedText);
        fixedDocument = fixedDocument.WithText(SourceText.From(fixedText));

        var diagnostics = await AfterTheSuppressor(fixedDocument, analyzer, ct);
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "The fixed code doesn't compile:\n" + string.Join("\n", errors));

        var warningsBefore = WarningsLeft(await AfterTheSuppressor(document, analyzer, ct), fix).ToList();
        var added = WarningsLeft(diagnostics, fix).GroupBy(d => d.Id).Where(same => same.Count() > warningsBefore.Count(d => d.Id == same.Key)).SelectMany(same => same).ToList();
        Assert.True(added.Count == 0, "The fixed code has warnings it didn't have before:\n" + string.Join("\n", added));

        var left = diagnostics.Where(d => fix.FixableDiagnosticIds.Contains(d.Id) && !d.IsSuppressed).ToList();
        if (someLeftAlone)
        {
            Assert.True(left.Count > 0, $"Nothing was left alone, so {nameof(someLeftAlone)} isn't needed.");
            var stillOffered = new List<Diagnostic>();
            foreach (var diagnostic in left)
            {
                var offered = false;
                await fix.RegisterCodeFixesAsync(new CodeFixContext(fixedDocument, diagnostic, (_, _) => offered = true, ct));
                if (offered)
                {
                    stillOffered.Add(diagnostic);
                }
            }

            left = stillOffered;
        }

        Assert.True(left.Count == 0, "The fixed code still has what the fix is for:\n" + string.Join("\n", left));
    }

    private static CSharpCompilation Compile(
        string text,
        OutputKind outputKind,
        bool allowUnsafe,
        IEnumerable<MetadataReference>? references,
        string assemblyName = "Test",
        IEnumerable<string>? disabledWarnings = null,
        IEnumerable<string>? enabled = null,
        IReadOnlyDictionary<string, string>? editorConfig = null,
        string? generated = null,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null,
        ReportDiagnostic generalOption = ReportDiagnostic.Default
    )
    {
        var trees = new[] { (Path: "Test.cs", Text: text), (Path: "Test.g.cs", Text: generated) }
            .Where(file => file.Text is not null)
            .Select(file => CSharpSyntaxTree.ParseText(file.Text!, new CSharpParseOptions(LanguageVersion.Latest), path: file.Path, cancellationToken: TestContext.Current.CancellationToken));

        return CSharpCompilation.Create(
            assemblyName,
            trees,
            FrameworkReferences.AddRange(references ?? []),
            new CSharpCompilationOptions(
                outputKind,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: allowUnsafe,
                specificDiagnosticOptions: (disabledWarnings ?? []).Select(id => KeyValuePair.Create(id, ReportDiagnostic.Suppress))
                    .Concat((enabled ?? []).Select(id => KeyValuePair.Create(id, ReportDiagnostic.Info)))
                    .Concat(diagnosticOptions ?? new Dictionary<string, ReportDiagnostic>()),
                generalDiagnosticOption: generalOption
            ).WithSyntaxTreeOptionsProvider(editorConfig is null ? null : new DiagnosticSeverities(editorConfig))
        );
    }

    private static Document CreateDocument(AdhocWorkspace workspace, string source, IEnumerable<string>? enabled, bool usings, IEnumerable<MetadataReference>? references = null)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Test",
                "Test",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    specificDiagnosticOptions: (enabled ?? []).Select(id => KeyValuePair.Create(id, ReportDiagnostic.Info))
                ),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: FrameworkReferences.AddRange(references ?? [])
            ))
            .AddDocument(documentId, "Test.cs", (usings ? Usings : "") + source + Types);
        return solution.GetDocument(documentId)!;
    }

    [GeneratedRegex(@"/\*!(?<id>[A-Z]+[0-9]+)\*/")]
    private static partial Regex DiagnosticMarker();

    private static Dictionary<int, bool> FindMarkers(string text)
    {
        var markers = new Dictionary<int, bool>();
        foreach (var (marker, suppressed) in new[] { (SuppressedMarker, true), (WarnsMarker, false) })
        {
            foreach (var i in Occurrences(text, marker))
            {
                var position = i + marker.Length;
                Assert.True(
                    string.CompareOrdinal(text, position, "switch", 0, "switch".Length) == 0,
                    $"line {LineOf(text, i)}: {marker} must be immediately followed by `switch`"
                );
                markers.Add(position, suppressed);
            }
        }

        return markers;
    }

    /// <summary>
    /// What <paramref name="fix"/> fixes in the document, from the compiler or from <paramref name="analyzer"/>, in the order they appear.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> FixableDiagnostics(Document document, CodeFixProvider fix, DiagnosticAnalyzer? analyzer, CancellationToken ct)
    {
        var compilation = (await document.Project.GetCompilationAsync(ct))!;
        var diagnostics = analyzer is null
            ? compilation.GetDiagnostics(ct)
            : await compilation.WithAnalyzers([analyzer], new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty)).GetAnalyzerDiagnosticsAsync(ct);
        return [.. diagnostics.Where(d => fix.FixableDiagnosticIds.Contains(d.Id)).OrderBy(d => d.Location.SourceSpan.Start)];
    }

    private static int LineOf(string text, int position) =>
        text.AsSpan(0, position).Count('\n') + 1;

    /// <summary>
    /// The running .NET 11 framework, which is what the snippets need for <c>union</c> (System.Runtime.CompilerServices.UnionAttribute and IUnion).
    /// </summary>
    private static ImmutableArray<MetadataReference> LoadFrameworkReferences()
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetDirectoryName(path) == frameworkDirectory)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    private static IEnumerable<int> Occurrences(string text, string marker)
    {
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + 1, StringComparison.Ordinal))
        {
            yield return i;
        }
    }

    private static async Task<string> SuppressionProblems(
        string text,
        bool allowUnsafe,
        IEnumerable<MetadataReference>? references,
        bool topLevelStatements,
        string diagnosticId,
        IEnumerable<string>? disabledWarnings,
        IReadOnlyDictionary<string, string>? editorConfig,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions,
        ReportDiagnostic generalOption
    )
    {
        var outputKind = topLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var compilation = Compile(text, outputKind, allowUnsafe, references, disabledWarnings: disabledWarnings, editorConfig: editorConfig, diagnosticOptions: diagnosticOptions, generalOption: generalOption);
        var tree = compilation.SyntaxTrees[0];

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !d.IsWarningAsError).ToList();
        if (errors.Count > 0)
        {
            return "The snippet doesn't compile:\n" + string.Join("\n", errors);
        }

        var expected = FindMarkers(text);
        if (expected.Count == 0)
        {
            return $"The snippet has no {SuppressedMarker} or {WarnsMarker} markers.";
        }

        var exceptions = new List<Exception>();
        var options = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new EditorConfig(editorConfig ?? new Dictionary<string, string>())),
            onAnalyzerException: (ex, _, _) => exceptions.Add(ex),
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: true
        );
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new UnionNarrowingSuppressor(), new MissingCasesAnalyzer());
        if (diagnosticId.StartsWith("IDE", StringComparison.Ordinal))
        {
            analyzers = analyzers.AddRange(IdePopulateSwitchAnalyzers);
        }

        var diagnostics = await compilation
            .WithAnalyzers(analyzers, options)
            .GetAllDiagnosticsAsync(TestContext.Current.CancellationToken);

        if (exceptions.Count > 0)
        {
            return "An analyzer threw:\n" + string.Join("\n", exceptions);
        }

        var analyzerFailures = diagnostics.Where(d => d.Id == "AD0001").ToList();
        if (analyzerFailures.Count > 0)
        {
            return "An analyzer failed:\n" + string.Join("\n", analyzerFailures);
        }

        var problems = new StringBuilder();
        var reported = diagnostics.Where(d => d.Id == diagnosticId).ToDictionary(d => SwitchKeywordAt(d.Location));
        var reportedInstead = diagnostics.Where(d => d.Id == ReportedInstead && !d.IsSuppressed && d.Severity >= DiagnosticSeverity.Warning)
            .GroupBy(d => SwitchKeywordAt(d.Location))
            .ToDictionary(reports => reports.Key, reports => reports.Max(d => d.Severity));

        foreach (var (position, shouldSuppress) in expected.OrderBy(e => e.Key))
        {
            var where = Where(tree, position);
            if (!reported.TryGetValue(position, out var diagnostic))
            {
                problems.AppendLine($"{where}: expected {diagnosticId} here, but it wasn't reported (the test proves nothing)");
                continue;
            }

            var replaced = diagnostic.IsSuppressed
                && diagnostic.GetSuppressionInfo(compilation)!.ProgrammaticSuppressions.All(s => SuppressedForReportedInstead.Contains(s.Descriptor.Id));
            var standsIn = reportedInstead.TryGetValue(position, out var insteadSeverity) && insteadSeverity >= diagnostic.Severity;
            
            if (replaced != standsIn)
            {
                problems.AppendLine(replaced
                    ? $"{where}: {diagnosticId} was suppressed for {ReportedInstead} to be reported instead, but {ReportedInstead} wasn't reported"
                    : $"{where}: {ReportedInstead} was reported, but {diagnosticId} was {(diagnostic.IsSuppressed ? "suppressed" : "left in place")} as well"
                );
            }

            var silenced = diagnostic.IsSuppressed && !replaced;
            if (silenced != shouldSuppress)
            {
                problems.AppendLine($"{where}: expected {diagnosticId} to be {(shouldSuppress ? "suppressed" : $"left in place (or reported as {ReportedInstead})")}, "
                    + $"but it was {(silenced ? "suppressed" : replaced ? $"reported as {ReportedInstead}" : "left in place")}"
                );
            }
        }

        foreach (var position in reported.Keys.Where(p => !expected.ContainsKey(p)).Order())
        {
            problems.AppendLine($"{Where(tree, position)}: {diagnosticId} reported on a switch with no marker");
        }

        return problems.ToString();
    }

    /// <summary>
    /// The position of the <c>switch</c> keyword of the switch a diagnostic is on.
    /// </summary>
    private static int SwitchKeywordAt(Location location)
    {
        var token = location.SourceTree!.GetRoot().FindToken(location.SourceSpan.Start);
        return token.Parent?.AncestorsAndSelf().Select(node => node switch
        {
            SwitchExpressionSyntax expression => expression.SwitchKeyword.SpanStart,
            SwitchStatementSyntax statement => statement.SwitchKeyword.SpanStart,
            _ => (int?)null,
        }).FirstOrDefault(position => position is not null) ?? location.SourceSpan.Start;
    }

    /// <summary>
    /// The warnings the suppressor leaves in place, apart from those <paramref name="fix"/> is for.
    /// </summary>
    private static IEnumerable<Diagnostic> WarningsLeft(IEnumerable<Diagnostic> diagnostics, CodeFixProvider fix) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning && !d.IsSuppressed && !fix.FixableDiagnosticIds.Contains(d.Id));

    /// <summary>
    /// Where a position is, for messages:
    /// its line, and the members and local functions it's in, like <c>line 12 (in Partly)</c> or <c>line 30 (in AfterTheGuard.Local)</c>.
    /// </summary>
    private static string Where(SyntaxTree tree, int position)
    {
        var line = tree.GetLineSpan(new TextSpan(position, 0)).StartLinePosition.Line + 1;
        var names = tree.GetRoot().FindToken(position).Parent?.AncestorsAndSelf()
            .Select(node => node switch
            {
                LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + constructor.ParameterList,
                BasePropertyDeclarationSyntax property => property switch
                {
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    EventDeclarationSyntax e => e.Identifier.ValueText,
                    _ => "this[]",
                },
                _ => null,
            })
            .OfType<string>()
            .Reverse()
            .ToList() ?? [];
        return names.Count == 0 ? $"line {line}" : $"line {line} (in {string.Join(".", names)})";
    }

    /// <summary>
    /// The snippet with every <c>/*because*/</c> statement replaced by an empty statement (keeping its line breaks, so line numbers stay the same), and the <c>/*warns*/</c> markers in the same members turned into <c>/*suppressed*/</c>.
    /// For top-level statements, the member is all of them.
    /// </summary>
    private static string WithoutTheReasons(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        var edits = new SortedDictionary<int, (int Length, string Replacement)>();
        var flipped = new HashSet<int>();

        foreach (var start in Occurrences(text, BecauseMarker))
        {
            var position = start + BecauseMarker.Length;
            var statement = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            Assert.True(statement?.SpanStart == position, $"line {LineOf(text, start)}: {BecauseMarker} must be immediately followed by a statement");

            var removed = text[start..statement.Span.End];
            edits.Add(start, (removed.Length, ";" + new string('\n', removed.Count(c => c == '\n'))));

            TextSpan? scope = statement.Ancestors().FirstOrDefault(a => a is GlobalStatementSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax) switch
            {
                GlobalStatementSyntax => TextSpan.FromBounds(
                    root.ChildNodes().OfType<GlobalStatementSyntax>().First().SpanStart,
                    root.ChildNodes().OfType<GlobalStatementSyntax>().Last().Span.End
                ),
                { } member => member.Span,
                null => null,
            };
            Assert.True(scope is not null, $"line {LineOf(text, start)}: {BecauseMarker} has to be in a method, constructor, property or top-level statement");

            var warns = Occurrences(text, WarnsMarker).Where(scope.Value.Contains).ToList();
            Assert.True(warns.Count > 0, $"line {LineOf(text, start)}: {BecauseMarker} has no {WarnsMarker} switch in its member to be the reason for");
            foreach (var w in warns.Where(flipped.Add))
            {
                edits.Add(w, (WarnsMarker.Length, SuppressedMarker));
            }
        }

        var result = new StringBuilder(text);
        var end = int.MaxValue;
        foreach (var (at, (length, replacement)) in edits.Reverse())
        {
            Assert.True(at + length <= end, $"line {LineOf(text, at)}: a {BecauseMarker} statement can't contain a {WarnsMarker} switch");
            result.Remove(at, length).Insert(at, replacement);
            end = at;
        }

        return result.ToString();
    }

    /// <summary>
    /// Applies <paramref name="fix"/> to the one diagnostic it fixes in <paramref name="source"/> (from the compiler, or from <paramref name="analyzer"/>) and checks the result is <paramref name="expected"/>, still compiles, and no longer has that diagnostic, nor any warning it didn't have, once the suppressor has run.
    /// With <paramref name="expected"/> null, checks that the fix isn't offered.
    /// </summary>
    /// <param name="enabled">Rules that are off by default, turned on.</param>
    /// <param name="usings">Whether to put <see cref="Usings"/> before the snippet.
    /// Without them, the snippet has only the usings it declares.</param>
    /// <param name="title">What the fix has to be called, if it matters.</param>
    /// <param name="references">Assemblies the snippet uses, from <see cref="CompileToMetadata"/>.</param>
    public static async Task CodeFix(
        CodeFixProvider fix, string source, string? expected, DiagnosticAnalyzer? analyzer = null, IEnumerable<string>? enabled = null, bool usings = true, string? title = null,
        IEnumerable<MetadataReference>? references = null
    )
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, source, enabled, usings, references);
        var diagnostic = Assert.Single(await FixableDiagnostics(document, fix, analyzer, ct));

        var actions = new List<CodeAction>();
        await fix.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct));
        if (expected is null)
        {
            Assert.Empty(actions);
            return;
        }

        var offered = Assert.Single(actions);
        if (title is not null)
        {
            Assert.Equal(title, offered.Title);
        }

        await CheckFixed(document, await Apply(offered, document, ct), expected, fix, analyzer, ct, usings: usings);
    }

    /// <summary>
    /// Compiles a library and returns it as a metadata reference, for unions declared in another assembly.
    /// </summary>
    public static MetadataReference CompileToMetadata(string source)
    {
        var compilation = Compile(source, OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false, references: null, assemblyName: "External");
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>
    /// Runs an analyzer over a snippet and checks its diagnostics against markers written immediately before where each one starts:
    /// <c>/*!UN0003*/default</c> means UN0003 is reported on the <c>default</c>.
    /// Every marker must have its diagnostic, and every diagnostic its marker.
    /// Returns the diagnostics in the order they appear, for checking messages.
    /// </summary>
    /// <param name="enabled">Rules that are off by default, turned on.</param>
    /// <param name="references">Assemblies the snippet uses, from <see cref="CompileToMetadata"/>.</param>
    /// <param name="generated">A second file, of generated code (<c>Test.g.cs</c>), which can't have markers: nothing may be reported in it.</param>
    public static async Task<IReadOnlyList<Diagnostic>> Diagnostics(
        DiagnosticAnalyzer analyzer, string source, bool topLevelStatements = false, IEnumerable<string>? enabled = null, IEnumerable<MetadataReference>? references = null,
        string? generated = null
    )
    {
        var text = Usings + source + Types;
        var outputKind = topLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var compilation = Compile(text, outputKind, allowUnsafe: false, references, enabled: enabled, generated: generated);
        var tree = compilation.SyntaxTrees[0];

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "The snippet doesn't compile:\n" + string.Join("\n", errors));

        var exceptions = new List<Exception>();
        var options = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            onAnalyzerException: (ex, _, _) => exceptions.Add(ex),
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false
        );
        var diagnostics = await compilation
            .WithAnalyzers([analyzer], options)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.True(exceptions.Count == 0, "The analyzer threw:\n" + string.Join("\n", exceptions));

        var ids = analyzer.SupportedDiagnostics.Select(d => d.Id).ToHashSet();
        var reported = diagnostics.Where(d => ids.Contains(d.Id)).OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        var expected = DiagnosticMarker().Matches(text).ToDictionary(m => m.Index + m.Length, m => m.Groups["id"].Value);

        var problems = new StringBuilder();
        foreach (var (position, id) in expected.OrderBy(e => e.Key))
        {
            if (!reported.Any(d => d.Id == id && d.Location.SourceSpan.Start == position))
            {
                problems.AppendLine($"{Where(tree, position)}: expected {id} here, but it wasn't reported");
            }
        }

        foreach (var diagnostic in reported)
        {
            if (diagnostic.Location.SourceTree != tree)
            {
                problems.AppendLine($"{diagnostic.Location.GetLineSpan()}: unexpected {diagnostic.Id} in generated code: {diagnostic.GetMessage()}");
            }
            else if (!expected.TryGetValue(diagnostic.Location.SourceSpan.Start, out var id) || id != diagnostic.Id)
            {
                problems.AppendLine($"{Where(tree, diagnostic.Location.SourceSpan.Start)}: unexpected {diagnostic.Id}: {diagnostic.GetMessage()}");
            }
        }

        Assert.True(problems.Length == 0, problems.ToString());
        return reported;
    }

    /// <summary>
    /// Applies <paramref name="fix"/>'s Fix All, for the whole document, to every diagnostic it fixes in <paramref name="source"/> (there have to be at least two), and checks the result the same way as <see cref="CodeFix"/>.
    /// </summary>
    /// <param name="enabled">Rules that are off by default, turned on.</param>
    /// <param name="someLeftAlone">Some diagnostics are left in place on purpose, because the fix isn't offered for them.
    /// Then what's checked is that the fix isn't offered for any that are left.</param>
    /// <param name="usings">Whether to put <see cref="Usings"/> before the snippet.
    /// Without them, the snippet has only the usings it declares.</param>
    public static async Task FixAll(
        CodeFixProvider fix, string source, string expected, DiagnosticAnalyzer? analyzer = null, IEnumerable<string>? enabled = null, bool someLeftAlone = false, bool usings = true
    )
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, source, enabled, usings);
        var diagnostics = await FixableDiagnostics(document, fix, analyzer, ct);
        Assert.True(diagnostics.Length >= 2, $"Fix All needs at least two diagnostics to fix, but there are {diagnostics.Length}. For one, use {nameof(CodeFix)}.");

        var actions = new List<CodeAction>();
        await fix.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostics[0], (action, _) => actions.Add(action), ct));
        var provider = fix.GetFixAllProvider();
        Assert.NotNull(provider);

        var context = new FixAllContext(document, fix, FixAllScope.Document, Assert.Single(actions).EquivalenceKey, fix.FixableDiagnosticIds, new GivenDiagnostics(diagnostics), ct);
        var action = await provider.GetFixAsync(context);
        Assert.NotNull(action);
        await CheckFixed(document, await Apply(action, document, ct), expected, fix, analyzer, ct, someLeftAlone, usings);
    }

    /// <summary>
    /// Compiles a snippet with and without the suppressor and checks every <paramref name="diagnosticId"/> against a marker written immediately before the <c>switch</c> keyword of its switch:
    /// <list type="bullet">
    /// <item><c>/*suppressed*/switch</c>: the compiler reports it here and the suppressor silences it.</item>
    /// <item><c>/*warns*/switch</c>: the compiler reports it here and it stays, or the switch gets UN0020 instead, which names what it misses where earlier code rules some of the rest out (and nothing else may suppress it).</item>
    /// </list>
    /// Every marker must have its diagnostic (so a test can't pass because the compiler was already happy), every diagnostic must have a marker, the snippet must compile, and the suppressor must not throw.
    /// UN0020 has to be reported on exactly the switches whose diagnostic is suppressed for it.
    /// (Roslyn itself rejects a suppression of any diagnostic other than the descriptor's.)
    /// <para>
    /// <c>/*because*/</c>, written immediately before a statement, says that statement is why the <c>/*warns*/</c> switches in the same member warn.
    /// The snippet is then checked a second time with every such statement taken out, and those switches have to be suppressed, so a <c>/*warns*/</c> can't pass for some other reason.
    /// </para>
    /// </summary>
    /// <param name="disabledWarnings">Warnings turned off for the compilation, as NoWarn would.</param>
    /// <param name="diagnosticOptions">Severities set for the compilation by ID, as WarningsAsErrors (<see cref="ReportDiagnostic.Error"/>) and WarningsNotAsErrors (<see cref="ReportDiagnostic.Default"/>) do.</param>
    /// <param name="generalOption">The severity for every warning, as TreatWarningsAsErrors (<see cref="ReportDiagnostic.Error"/>) sets it.</param>
    /// <param name="editorConfig">.editorconfig settings for every file, which the compiler reads where they turn a diagnostic off (<c>dotnet_diagnostic.UN0020.severity = none</c>).</param>
    /// <param name="diagnosticId">What the markers are about:
    /// CS8509 from the compiler (or CS8846, CS8655, CS8847), or IDE0072 or IDE0010 from the IDE's populate-switch analyzers, which are run too.
    /// Each is matched to the <c>switch</c> keyword of its switch.</param>
    public static async Task Suppressions(
        string source,
        bool allowUnsafe = false,
        IEnumerable<MetadataReference>? references = null,
        bool topLevelStatements = false,
        string diagnosticId = "CS8509",
        IEnumerable<string>? disabledWarnings = null,
        IReadOnlyDictionary<string, string>? editorConfig = null,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null,
        ReportDiagnostic generalOption = ReportDiagnostic.Default
    )
    {
        var text = Usings + source + Types;
        var problems = await SuppressionProblems(text, allowUnsafe, references, topLevelStatements, diagnosticId, disabledWarnings, editorConfig, diagnosticOptions, generalOption);
        Assert.True(problems.Length == 0, problems);

        if (!text.Contains(BecauseMarker, StringComparison.Ordinal))
        {
            return;
        }

        problems = await SuppressionProblems(WithoutTheReasons(text), allowUnsafe, references, topLevelStatements, diagnosticId, disabledWarnings, editorConfig, diagnosticOptions, generalOption);
        Assert.True(problems.Length == 0, $"With the {BecauseMarker} statements taken out, the switches they're the reason for should be suppressed:\n{problems}");
    }
}

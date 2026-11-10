# Contributing to UnionNarrowing

**Please open an issue or start a discussion before working on a PR**, especially for anything beyond a small, obvious fix. It's a lot easier to agree on the shape of a change before it's written than to rework a finished PR.

Good things to open an issue for:

* **A suppression that hides a real missing case.** This is the most serious kind of bug here, because the package's whole promise is that it only suppresses what it can prove. Please include the code.
* A switch that should be suppressed but isn't: narrowing that the package doesn't follow yet. Include the code, and say where the guard is relative to the switch.
* An analyzer that reports something it shouldn't, or misses something it should, or a code fix that produces wrong or uncompilable code.
* Anything in [the README](README.md) that's unclear or out of date.

## Development setup

You'll need the .NET 11 SDK pinned in [global.json](global.json) (`11.0.100` or later). The `Microsoft.CodeAnalysis.*` packages come from the `dotnet-tools` feed that [nuget.config](nuget.config) adds, so there's nothing else to set up.

```bash
dotnet build
dotnet test
```

The solution is [UnionNarrowing.slnx](UnionNarrowing.slnx), with the following projects:

* **UnionNarrowing:** packs the two projects below into the `UnionNarrowing` NuGet package. It has no code of its own.
* **UnionNarrowing.Suppressors:** the suppressor and the analyzers. The narrowing analysis they share is in `Analysis/`. It targets `netstandard2.0` and references only the compiler layer of Roslyn, because the command-line compiler loads it.
* **UnionNarrowing.CodeFixes:** the code fixes. They live apart from the analyzers because they need Roslyn's Workspaces layer, which the compiler doesn't load.
* **UnionNarrowing.Sample:** builds with the suppressor loaded the way a package consumer gets it, with warnings as errors. It only builds if every switch in it is suppressed.
* **UnionNarrowing.Tests:** xUnit tests.

The shared analysis in `UnionNarrowing.Suppressors/Analysis/` builds up in layers, each using only the ones before it:

* `Symbols`, `Functions` and `Statements`: what's known about types and attributes, functions and calls, and statement lists and jumps.
* `CaseSpace` and `CaseSet`: the cases a value can be in, and sets of them. `TypeArguments`: what generic code's type parameters stand for where it's read for a call (a space carries them, so a type written in that code is read through them). `TrackedValue`: a value the analysis follows, as a path of `Step`s from a local, parameter, `this` or static member.
* `WriteAnalysis` and `VariableUsage`: what can write a value, and how a local or parameter is used.
* `Sequences`: what LINQ, and collection methods like it, do with a sequence's elements.
* `Facts`: what a pattern or condition says about a value's cases.
* `Narrowing`: what earlier code rules out at a given point, walking outward from it. Its partial files cover what several values rule out together (`Narrowing.Joint.cs`), what callers pass a function (`Narrowing.Calls.cs`), what a value was assigned or copied from (`Narrowing.Values.cs`), and LINQ chains (`Narrowing.Linq.cs`).
* `SwitchCoverage` and `NestedCoverage`: whether a switch handles every case that's left, which is what the suppressor and most of the analyzers ask. `Handling`: what's handled ahead of a throwing default (UN0002).

`Limits` holds every limit on how far the analysis goes. The README states the ones a user can run into, so change both together.

A `Session` is one run of the analysis from one entry point: the suppressor's `ReportSuppressions`, an analyzer's compilation, or a code fix. It holds the .editorconfig options the run reads and what it works out once and reuses, like how a variable is used. Every `TrackedValue` carries its session, so nothing reads options or caches from static state. An entry point makes one and passes it in.

## Tests

The tests are in three folders, one for each part of the package:

* **Suppressor:** what the suppressor follows. The classes follow the headings of the README's [Suppressions](README.md#suppressions) reference. For example, GuardTests is [Tests that rule a case out](README.md#tests-that-rule-a-case-out), ControlFlowTests and PositionTests are [Where the test can be](README.md#where-the-test-can-be) (before the switch, and around it), and MutationTests is [What stops the narrowing](README.md#what-stops-the-narrowing). Put a test in the class for the README section it's about.
* **Analyzers:** a class for each analyzer.
* **CodeFixes:** a class for each code fix.

The folders don't change the namespace, so `-class UnionNarrowing.Tests.GuardTests` finds a class wherever it is.

Each test compiles a snippet against the shared `Pet` union (`Cat`, `Dog`, `Bird`) and `IFlier` interface declared in [Verify.cs](UnionNarrowing.Tests/Verify.cs). Declarations that several snippets use (a closed `Shape` hierarchy, a `Result` union with a closed case type, a `Young` union with a case that holds a union) are in [Snippets.cs](UnionNarrowing.Tests/Snippets.cs), to add after a snippet: `""" + Snippets.Result`. Each test uses one of these helpers:

* `Verify.Suppressions` for the suppressor. Write `/*suppressed*/` or `/*warns*/` immediately before the `switch` keyword of every switch that gets CS8509. The test fails if a marker has no CS8509 or a CS8509 has no marker, so it can't pass just because the compiler was already happy.
* `Verify.Diagnostics` for the analyzers. Write `/*!UN0002*/` immediately before where each diagnostic should start. Every marker needs its diagnostic, and every diagnostic its marker. It returns the diagnostics in the order they appear, for checking their messages.
* `Verify.CodeFix` for the code fixes. Pass the source and the expected result, or `null` to check that the fix isn't offered. As well as comparing the result, it parses the fixed text again (as the IDE would) and checks that it compiles, that it has no warning the original didn't, and that what the fix was for is gone once the suppressor has run, so the fix and the suppressor have to agree. It optionally takes the fix's `title:` and `references:` to compile against.
* `Verify.FixAll` for a code fix's Fix All, on a snippet with at least two diagnostics the fix is for (`usings:` as for `CodeFix`).

A failure gives the line and the method in the snippet it's in, so give the snippet's methods names that say what each is about.

When you teach the suppressor a new kind of narrowing, also add the nearby shapes that must keep warning (`/*warns*/`): a write between the guard and the switch, a guard that only rules out some values of a case, and so on. A missed suppression is an annoyance, but a wrong one hides a real bug.

A `/*warns*/` can pass for the wrong reason, though: the suppressor might give up on the switch for some reason other than the one the test is about. When a statement is the reason, write `/*because*/` immediately before it. `Verify.Suppressions` then checks the snippet a second time with that statement taken out, and the `/*warns*/` switches in the same method have to be suppressed:

```csharp
int M(Pet pet)
{
    if (pet is Bird) return 0;
    /*because*/pet = new Bird("Polly");
    return pet /*warns*/switch { Cat => 1, Dog => 2 };
}
```

## Making a change

1. If there is an open issue you would like to implement, please start a conversation on that issue stating you would like to take it on.
2. Fork the repo and branch from `main`.
3. Make your change, with tests. If it changes what the package does, update the [Reference](README.md#reference) section of the README, which lists exactly what is and isn't followed.
4. A new diagnostic needs a descriptor in [Descriptors.cs](UnionNarrowing.Suppressors/Analyzers/Descriptors.cs) and a row in [AnalyzerReleases.Unshipped.md](UnionNarrowing.Suppressors/AnalyzerReleases.Unshipped.md). The build fails without the row.
5. `dotnet build` and `dotnet test` should both be clean, without warnings.
6. Open a PR against `main`. CI ([build.yml](.github/workflows/build.yml)) builds, tests and packs every PR.

By submitting a PR, you agree your contribution is licensed under this project's [MIT license](LICENSE).

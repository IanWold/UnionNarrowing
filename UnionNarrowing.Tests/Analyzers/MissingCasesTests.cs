using Microsoft.CodeAnalysis;
using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0020: a switch that misses cases, where earlier code rules out others the compiler would count as missing, reported with the cases that are left instead of CS8509.
/// </summary>
public class MissingCasesTests
{
    /// <summary>
    /// A union that gained a case, and a switch that a guard leaves missing it: CS8509 would give the guarded case as its example.
    /// <c>{0}</c> is where the marker goes, before the switch.
    /// </summary>
    private const string AddedCase = """
        class C
        {
            int Status(Error error)
            {
                if (error is RemoteServiceError) return 502;
                return error {0}switch { ValidationError => 400, PricingError => 422 };
            }
        }

        public record ValidationError;
        public record PricingError;
        public record RemoteServiceError;
        public record OutsideBusinessHours;
        public union Error(ValidationError, PricingError, RemoteServiceError, OutsideBusinessHours);
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source) =>
        Verify.Diagnostics(new MissingCasesAnalyzer(), source);

    [Fact]
    public Task CS8509_is_suppressed_for_it() =>
        Verify.Suppressions(AddedCase.Replace("{0}", "/*warns*/"));

    [Fact]
    public async Task CS8509_stays_where_UN0020_is_turned_off()
    {
        var source = AddedCase.Replace("{0}", "/*warns*/");

        await Verify.Suppressions(source, disabledWarnings: ["UN0020"]);
        await Verify.Suppressions(source, editorConfig: new Dictionary<string, string> { ["dotnet_diagnostic.UN0020.severity"] = "none" });
        await Verify.Suppressions(source, editorConfig: new Dictionary<string, string> { ["dotnet_analyzer_diagnostic.category-Reliability.severity"] = "none" });
    }

    /// <summary>
    /// Where UN0020 wouldn't be seen as a warning, CS8509 has to be.
    /// </summary>
    [Fact]
    public async Task CS8509_stays_where_UN0020_is_hidden()
    {
        var source = AddedCase.Replace("{0}", "/*warns*/");

        await Verify.Suppressions(source, editorConfig: new Dictionary<string, string> { ["dotnet_diagnostic.UN0020.severity"] = "silent" });
        await Verify.Suppressions(source, editorConfig: new Dictionary<string, string> { ["dotnet_diagnostic.UN0020.severity"] = "suggestion" });
        await Verify.Suppressions("#pragma warning disable UN0020\n" + source);
        await Verify.Suppressions(source.Replace("    int Status(", "    [System.Diagnostics.CodeAnalysis.SuppressMessage(\"Reliability\", \"UN0020\")]\n    int Status("));
    }

    /// <summary>
    /// WarningsNotAsErrors leaves UN0020 a warning where TreatWarningsAsErrors makes CS8509 an error, and lets .editorconfig still turn it off.
    /// </summary>
    [Fact]
    public async Task CS8509_stays_where_UN0020_is_not_made_an_error()
    {
        var source = AddedCase.Replace("{0}", "/*warns*/");
        var notAsErrors = new Dictionary<string, ReportDiagnostic> { ["UN0020"] = ReportDiagnostic.Default };

        await Verify.Suppressions(source, diagnosticOptions: notAsErrors, generalOption: ReportDiagnostic.Error);
        await Verify.Suppressions(source, diagnosticOptions: notAsErrors, editorConfig: new Dictionary<string, string> { ["dotnet_diagnostic.UN0020.severity"] = "none" });
        await Verify.Suppressions(source, generalOption: ReportDiagnostic.Error);
    }

    /// <summary>
    /// <c>[SuppressMessage]</c> on a property, an indexer or an event covers the code in its accessors.
    /// </summary>
    [Fact]
    public Task CS8509_stays_where_UN0020_is_suppressed_on_a_member_with_accessors() =>
        Verify.Suppressions("""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                private readonly Pet _pet = new Dog("Rex");

                [SuppressMessage("Reliability", "UN0020")]
                public int Property => _pet is Bird ? 0 : _pet /*warns*/switch { Cat => 1 };

                [SuppressMessage("Reliability", "UN0020")]
                public int Accessor
                {
                    get
                    {
                        if (_pet is Bird) return 0;
                        return _pet /*warns*/switch { Cat => 1 };
                    }
                }

                [SuppressMessage("Reliability", "UN0020")]
                public int this[int i] => _pet is Bird ? i : _pet /*warns*/switch { Cat => 1 };

                [SuppressMessage("Reliability", "UN0020")]
                public event Action? Changed
                {
                    add { if (_pet is not Bird) _ = _pet /*warns*/switch { Cat => 1 }; }
                    remove { }
                }
            }
            """
        );

    [Fact]
    public async Task Cases_left_inside_the_cases()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Young young)
                {
                    if (young is Dog) return 0;
                    return young /*!UN0020*/switch { Kitten { Toy: Ball } => 1 };
                }
            }
            """ + Snippets.Toys
        );

        Assert.Equal("The switch doesn't handle Kitten { Toy: Mouse }, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    /// <summary>
    /// A guard that rules out a combination inside a case, where the arms look inside: the compiler's example can be that combination.
    /// </summary>
    [Fact]
    public async Task Combinations_left_inside_a_case_a_guard_looks_into()
    {
        const string source = """
            public record StepFailed(int ExitCode);
            public record Timeout;
            public record Cancelled;
            public record Infra;
            public union Reason(StepFailed, Timeout, Cancelled, Infra);
            public record Success;
            public record Failed(Reason Reason);
            public union RunResult(Success, Failed);

            class C
            {
                int ExitCode(RunResult r)
                {
                    if (r is Failed { Reason: Cancelled }) return 130;
                    return r {0}switch { Success => 0, Failed { Reason: StepFailed s } => s.ExitCode, Failed { Reason: Timeout } => 124 };
                }
            }
            """;

        await Verify.Suppressions(source.Replace("{0}", "/*warns*/"));
        var diagnostics = await Diagnostics(source.Replace("{0}", "/*!UN0020*/"));
        Assert.Equal("The switch doesn't handle Failed { Reason: Infra }, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public async Task Combinations_left_in_what_the_value_is_a_member_of()
    {
        const string source = """
            public record Submitted;
            public record Paid;
            public record Shipped;
            public record OnHold;
            public union Status(Submitted, Paid, Shipped, OnHold);
            public sealed record Order(int Number, Status Status);

            class C
            {
                string Label(Order order)
                {
                    if (order is { Status: Paid or Shipped }) return "done";
                    return order {0}switch { { Status: Submitted } => "new" };
                }
            }
            """;

        await Verify.Suppressions(source.Replace("{0}", "/*warns*/"));
        var diagnostics = await Diagnostics(source.Replace("{0}", "/*!UN0020*/"));
        Assert.Equal("The switch doesn't handle { Status: OnHold }, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public async Task Cases_left_with_null()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Shape? shape)
                {
                    if (shape is Circle) return 0;
                    return shape /*!UN0020*/switch { Square => 1 };
                }
            }
            """ + Snippets.Shapes
        );

        Assert.Equal("The switch doesn't handle Triangle, null, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public async Task Combinations_left_in_a_tuple()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) /*!UN0020*/switch { (Cat, _) => 1, (Dog, Cat) => 2 };
                }
            }
            """
        );

        Assert.Equal("The switch doesn't handle (Dog, Dog or Bird), which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public Task Not_reported_where_the_compiler_s_example_is_one_that_s_left() =>
        Diagnostics("""
            class C
            {
                int HandledAnyway(Pet pet)
                {
                    if (pet is Cat) return 0;
                    return pet switch { Cat => 1, Dog => 2 };
                }

                int NothingRuledOut(Pet pet) => pet switch { Cat => 1 };

                int RuledOutThenWritten(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    pet = other;
                    return pet switch { Cat => 1 };
                }

                int EverythingLeftHandled(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2 };
                }

                int ThrowingDefault(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, _ => throw new InvalidOperationException() };
                }
            }
            """
        );

    [Fact]
    public async Task Reported_with_the_cases_earlier_code_leaves()
    {
        var diagnostics = await Diagnostics(AddedCase.Replace("{0}", "/*!UN0020*/"));

        Assert.Equal("The switch doesn't handle OutsideBusinessHours, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public async Task Reported_with_when_arms()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return pet /*!UN0020*/switch { Cat => 1, _ when flag => 2 };
                }
            }
            """
        );

        Assert.Equal("The switch doesn't handle Dog, which earlier code doesn't rule out", diagnostics.Single().GetMessage());
    }

    [Fact]
    public Task Suppressions_with_when_arms() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, _ when flag => 2 };
                }
            }
            """, diagnosticId: "CS8846"
        );
}

using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0005: a switch on, or test of, a union's Value that could be on the union itself.
/// </summary>
public class SwitchOnValueTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source, IEnumerable<string>? enabled = null) =>
        Verify.Diagnostics(new SwitchOnValueAnalyzer(), source, enabled: enabled);

    [Fact]
    public Task Labels_the_union_does_not_take_and_class_unions() =>
        Diagnostics("""
            [System.Runtime.CompilerServices.Union]
            sealed class CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog(Cat cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                public object? Value { get; }
            }

            union Number(int, string);

            class C
            {
                // A switch statement with a relational label.
                int M(Number n)
                {
                    switch (n.Value)
                    {
                        case > 0: return 1;
                        case string: return 2;
                        default: return 0;
                    }
                }

                // On a class union, Value throws on null, where a switch on the union doesn't.
                int N(CatOrDog pet) => pet.Value switch { Cat => 1, _ => 0 };
            }
            """
        );

    [Fact]
    public Task Patterns_that_mean_something_else_on_the_union() =>
        Diagnostics("""
            class C
            {
                // On the union, `var` binds the union and `{ }` is always true.
                string M(Pet pet) => pet.Value switch { Cat => "cat", var other => other?.ToString() ?? "" };
                int N(Pet pet) => pet.Value switch { Cat => 1, { } => 2, null => 3 };
            }
            """
        );

    [Fact]
    public Task Patterns_the_union_does_not_allow() =>
        Diagnostics("""
            class C
            {
                // `pet switch { string => ... }` doesn't compile: no case type can be a string. Nor can any be 5.
                int M(Pet pet) => pet.Value switch { string => 1, Cat => 2, _ => 0 };
                int N(Pet pet) => pet.Value switch { 5 => 1, Cat => 2, _ => 0 };
            }
            """
        );

    [Fact]
    public async Task Switch_expression_and_statement_on_Value()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet) => /*!UN0005*/pet.Value switch { Cat c => c.Name.Length, Dog => 2, Bird or null => 3, _ => 0 };

                int N(Pet pet)
                {
                    switch (/*!UN0005*/pet.Value)
                    {
                        case Cat:
                            return 1;
                        case null:
                            return 0;
                        default:
                            return 2;
                    }
                }
            }
            """
        );

        Assert.Equal("Switch on 'pet' instead of 'pet.Value', so the compiler checks that every case is handled", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Tests_on_Value()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet pet)
                {
                    var a = /*!UN0005*/pet.Value is Cat;
                    var b = /*!UN0005*/pet.Value is Cat or Dog;
                    var c = /*!UN0005*/pet.Value is Cat cat ? cat.Name : "";
                    var d = /*!UN0005*/pet.Value is not null;

                    // Means something else on the union, or isn't allowed on it.
                    var e = pet.Value is { };
                    var f = pet.Value is var v;
                    var g = pet.Value is string;
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}");
                }
            }
            """
        );

        Assert.Equal("Test 'pet' instead of 'pet.Value', so the compiler checks that it's one of the union's cases", diagnostics[0].GetMessage());
    }

    [Fact]
    public Task Value_of_something_other_than_a_union() =>
        Diagnostics("""
            class Box { public object? Value { get; set; } }

            class C
            {
                int M(int? n) => n.Value switch { 1 => 1, _ => 0 };
                int N(Box box) => box.Value switch { Cat => 1, _ => 0 };
            }
            """
        );
}

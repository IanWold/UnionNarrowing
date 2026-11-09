using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0002 (cases fall through to a throwing default) and UN0016 (a throwing default only catches cases added later).
/// </summary>
public class ThrowingDefaultTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source, IEnumerable<string>? enabled = null) =>
        Verify.Diagnostics(new ThrowingDefaultAnalyzer(), source, enabled: enabled);

    [Fact]
    public Task Arm_with_a_when_clause_does_not_handle_its_case() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet) => pet /*!UN0002*/switch
                {
                    Cat c when c.Name == "Tom" => 1,
                    Dog or Bird => 2,
                    _ => throw new InvalidOperationException(),
                };
            }
            """
        );

    [Fact]
    public Task Cases_ruled_out_before_the_switch() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2, _ => throw new InvalidOperationException() };
                }

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Dog:
                            return 2;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """
        );

    [Fact]
    public async Task Chain_of_ifs_that_ends_in_a_throw()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog d && d.Name.Length > 0) return 2;
                    /*!UN0002*/else throw new InvalidOperationException();
                }

                int Handled(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog or Bird) return 2;
                    else throw new InvalidOperationException();
                }

                int RuledOut(Pet pet)
                {
                    if (pet is Bird) return 0;
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else throw new InvalidOperationException();
                }

                // A single test and an else is more likely a precondition.
                int Single(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else throw new InvalidOperationException();
                }

                int DoesSomething(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else return 0;
                }
            }
            """
        );

        Assert.Equal("The throwing else catches Dog, Bird, which the chain of ifs doesn't otherwise handle", diagnostics.Single().GetMessage());
    }

    [Fact]
    public Task Closed_hierarchy() =>
        Diagnostics("""
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;

            class C
            {
                int M(Shape shape) => shape /*!UN0002*/switch { Circle => 1, _ => throw new InvalidOperationException() };
            }
            """
        );

    [Fact]
    public Task Defaults_that_do_something() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, _ => 0 };

                int N(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            Console.WriteLine("not a cat");
                            return 0;
                    }
                }

                int O(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                    }
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task Else_chain_whose_condition_reassigns_the_value() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet, Func<Pet> next)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog || (pet = next()) is Bird) return 2;
                    /*!UN0002*/else throw new InvalidOperationException();
                }
            }
            """
        );

    [Fact]
    public async Task Names_the_parts_of_a_case_type_it_catches()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int All(Result r) => r /*!UN0002*/switch { Ok => 1, _ => throw new InvalidOperationException() };
                int Part(Result r) => r /*!UN0002*/switch { Ok => 1, NotFound => 2, _ => throw new InvalidOperationException() };
            }
            """ + Snippets.Result
        );

        Assert.Equal(
            ["The throwing discard arm catches Error, which the switch doesn't otherwise handle", "The throwing discard arm catches Timeout, which the switch doesn't otherwise handle"],
            diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.GetMessage())
        );
    }

    [Fact]
    public async Task Throw_after_a_switch_statement_or_ifs()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int Switch(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog: return 2;
                    }
                    /*!UN0002*/throw new InvalidOperationException();
                }

                int Ifs(Pet pet)
                {
                    if (pet is Cat) return 1;
                    if (pet is Dog) return 2;
                    /*!UN0002*/throw new InvalidOperationException();
                }

                int ElseIfs(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    /*!UN0002*/throw new InvalidOperationException();
                }

                // Only there for definite return.
                int Every(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog: return 2;
                        case Bird: return 3;
                    }
                    throw new InvalidOperationException();
                }

                int RuledOut(Pet pet)
                {
                    if (pet is Bird) return 0;
                    if (pet is Cat) return 1;
                    if (pet is Dog) return 2;
                    throw new InvalidOperationException();
                }

                // A section breaks out, so the throw isn't just for what no label matches.
                int Breaks(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: Console.WriteLine(); break;
                        case Dog: return 2;
                    }
                    throw new InvalidOperationException();
                }

                // A single test is more likely a precondition.
                int Single(Pet pet)
                {
                    if (pet is Cat) return 1;
                    throw new InvalidOperationException();
                }
            }
            """
        );

        Assert.Contains("The throwing statement after the switch catches Bird, which the switch doesn't otherwise handle", diagnostics.Select(d => d.GetMessage()));
        Assert.Contains("The throwing statement after the ifs catches Bird, which the chain of ifs doesn't otherwise handle", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Throwing_default_on_a_switch_that_handles_every_case()
    {
        var diagnostics = await Diagnostics("""
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;

            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3, /*!UN0016*/_ => throw new InvalidOperationException() };

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2, /*!UN0016*/_ => throw new InvalidOperationException() };
                }

                // It also catches a null shape, which the switch would otherwise have to handle.
                int O(Shape? shape) => shape switch { Circle => 1, Square => 2, _ => throw new InvalidOperationException() };

                // It catches Bird.
                int P(Pet pet) => pet /*!UN0002*/switch { Cat => 1, Dog => 2, _ => throw new InvalidOperationException() };
            }
            """, enabled: ["UN0016"]
        );

        Assert.Contains("This throwing discard arm only catches cases added to Pet later, which the compiler would otherwise report as missing", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Throwing_default_on_a_tuple_switch()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet a, Pet b) => (a, b) /*!UN0002*/switch { (Cat, _) => 1, _ => throw new InvalidOperationException() };

                int N(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) switch { (Cat, _) => 1, (Dog, _) => 2, /*!UN0016*/_ => throw new InvalidOperationException() };
                }

                int O(Pet a, bool flag)
                {
                    /*!UN0002*/switch (a, flag)
                    {
                        case (Cat, true): return 1;
                        default: throw new InvalidOperationException();
                    }
                }
            }
            """, enabled: ["UN0016"]
        );

        Assert.Contains("The throwing discard arm catches (Dog, _), (Bird, _), which the switch doesn't otherwise handle", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Throwing_default_section_after_every_case() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    // The default is only there for definite return (CS0161).
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Dog or Bird:
                            return 2;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """
        );

    [Fact]
    public Task Throwing_default_section_that_catches_a_case() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    /*!UN0002*/switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Dog:
                            return 2;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """
        );

    [Fact]
    public async Task Throwing_discard_arm_that_catches_a_case()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet) => pet /*!UN0002*/switch { Cat => 1, _ => throw new InvalidOperationException() };
            }
            """
        );

        Assert.Equal("The throwing discard arm catches Dog, Bird, which the switch doesn't otherwise handle", diagnostics.Single().GetMessage());
    }
}

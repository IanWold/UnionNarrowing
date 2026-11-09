using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0004: a switch arm, case label or test that can't match, because earlier code ruled out what it matches.
/// </summary>
public class DeadArmTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source, IEnumerable<string>? enabled = null) =>
        Verify.Diagnostics(new DeadArmAnalyzer(), source, enabled: enabled);

    [Fact]
    public async Task Arm_for_a_case_ruled_out_before_the_switch()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2, /*!UN0004*/Bird b => b.Name.Length };
                }
            }
            """
        );

        Assert.Equal("This arm can't match: earlier code already ruled out Bird", diagnostics.Single().GetMessage());
    }

    [Fact]
    public async Task Arms_for_parts_of_a_case_type()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int Leaf(Result r)
                {
                    if (r is Error) return 0;
                    return r switch { Ok => 1, /*!UN0004*/NotFound => 2, _ => 3 };
                }

                // `IComparable` can still match a bool, though it can't match a string any more.
                int Interface(Setting s)
                {
                    if (s is string) return 0;
                    return s switch { IComparable => 1 };
                }
            }
            public union Setting(bool, string);
            """ + Snippets.Result
        );

        Assert.Equal("This arm can't match: earlier code already ruled out NotFound", diagnostics.Single().GetMessage());
    }

    [Fact]
    public Task Arms_that_can_still_match() =>
        Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog or Bird => 2 };
                }

                // Matches a default(Pet), whose Value is null.
                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2, null => 3 };
                }

                int O(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3 };
            }
            """
        );

    [Fact]
    public async Task Case_label_for_a_case_ruled_out_before_the_switch()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        /*!UN0004*/case Bird b when b.Name.Length > 3:
                        case Dog:
                            return 2;
                        /*!UN0004*/case Bird:
                            return 3;
                        case null:
                            return 4;
                        default:
                            return 5;
                    }
                }
            }
            """
        );

        Assert.Equal("This case can't match: earlier code already ruled out Bird", diagnostics[0].GetMessage());
    }

    [Fact]
    public Task Nested_arms_and_tuple_tests_that_can_not_match() =>
        Diagnostics("""
            public record Ball; public record Mouse;
            public union Toy(Ball, Mouse);
            public record Kitten(Toy Toy);
            public union Young(Kitten, Dog);
            record Owner(Pet Pet);

            class C
            {
                int M(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y switch { /*!UN0004*/Kitten { Toy: Ball } => 9, Kitten => 1, Dog => 2 };
                }

                int N(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner switch { /*!UN0004*/{ Pet: Bird } => 9, _ => 1 };
                }

                // Could still match a Kitten with a Mouse.
                int O(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y switch { Kitten { Toy: Ball or Mouse } => 1, Dog => 2 };
                }

                bool P(Pet a, Pet b)
                {
                    if (a is Bird) return false;
                    return /*!UN0004*/(a, b) is (Bird, _);
                }

                bool Q(Pet a, Pet b)
                {
                    if (a is Bird) return false;
                    return (a, b) is (Bird or Cat, _);
                }
            }
            """
        );

    [Fact]
    public async Task Test_that_can_not_be_true()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    if (/*!UN0004*/pet is Bird b) return 9;
                    return /*!UN0004*/pet is Bird { Name: "Polly" } ? 9 : 1;
                }

                // A Cat or Dog could be of a derived type that's an IFlier.
                bool Interface(Pet pet) => pet is not Bird && pet is IFlier;

                bool N(Pet pet) => pet is not Bird && /*!UN0004*/pet is Bird;

                // Still could be.
                bool O(Pet pet) => pet is not Bird && pet is Cat;

                // `is null` matches a default union, which nothing ruled out.
                bool P(Pet pet) => pet is not Bird && pet is Bird or null;
            }
            """
        );

        Assert.Equal("This test can't match: earlier code already ruled out Bird", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Tuple_arm_for_a_combination_ruled_out_before_the_switch()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) switch { /*!UN0004*/(Bird, _) => 9, (Cat, _) => 1, (Dog, _) => 2 };
                }

                int Together(Pet a, Pet b)
                {
                    if (a is Bird && b is Bird) return 0;
                    return (a, b) switch { (Cat or Dog, _) => 1, /*!UN0004*/(Bird, Bird) => 9, (Bird, _) => 2 };
                }

                // (Bird, Cat) is still possible.
                int Partly(Pet a, Pet b)
                {
                    if (a is Bird && b is Bird) return 0;
                    return (a, b) switch { (Bird, _) => 2, (Cat or Dog, _) => 1 };
                }

                // Its Bird combinations are matched by the earlier arm, and the rest are ruled out.
                int Mixed(Pet a, bool flag)
                {
                    if (a is Dog) return 0;
                    return (a, flag) switch { (Bird, true) => 1, /*!UN0004*/(Bird or Dog, true) => 9, (_, _) => 2 };
                }
            }
            """
        );

        Assert.Equal("This arm can't match: earlier code already ruled out (Bird, _)", diagnostics[0].GetMessage());
    }

    [Fact]
    public Task Value_the_analysis_can_not_follow() =>
        Diagnostics("""
            class C
            {
                Pet _pet;

                void Reset() => _pet = new Bird("Polly");

                int M()
                {
                    if (_pet is Bird) return 0;
                    Reset();
                    return _pet switch { Cat => 1, Dog => 2, Bird => 3 };
                }
            }
            """
        );
}
